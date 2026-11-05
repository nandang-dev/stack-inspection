using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Domain;

namespace StackInspection.Infrastructure.Vision;

/// <summary>
/// Recognizer teks satu baris PP-OCR (mis. <c>ch_PP-OCRv4_rec_infer.onnx</c> dari RapidOCR, PaddleOCR/Baidu, Apache 2.0)
/// dengan ONNX Runtime CPU, tanpa deteksi teks. Model: <c>&lt;Vision:ModelsDirectory&gt;/&lt;Vision:LabelRecognizerModel&gt;/*.onnx</c>,
/// daftar karakter dari metadata model (<c>character</c>). Preprocess sama dengan PaddleOCR/RapidOCR: potongan BGR
/// di-resize ke tinggi <see cref="LineHeight"/> (lebar proporsional, INTER_LINEAR), (x/255 − 0,5)/0,5, NCHW, sisa
/// lebar diisi 0; satu batch selebar potongan terlebar (minimal <see cref="MinBatchWidth"/>). Decode CTC greedy;
/// keyakinan = rata-rata peluang karakter yang dipilih. Model tidak ditemukan → warning di log, semua hasil kosong.
/// </summary>
public sealed partial class OnnxTextLineRecognizer : ITextLineRecognizer, IDisposable
{
    public const int LineHeight = 48;

    public const int MinBatchWidth = 320;

    private const int MaxBatchWidth = 1280;
    private const int BatchSize = 32;

    private readonly Lazy<(InferenceSession Session, string[] Characters)?> _model;
    private readonly ILogger<OnnxTextLineRecognizer> _logger;

    public OnnxTextLineRecognizer(IOptions<VisionOptions> options, ILogger<OnnxTextLineRecognizer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        string name = options.Value.LabelRecognizerModel;
        string folder = Path.Combine(OnnxCartonDetector.ResolvePath(options.Value.ModelsDirectory), name);
        int threads = options.Value.Threads;
        _model = new Lazy<(InferenceSession, string[])?>(() => string.IsNullOrWhiteSpace(name) ? null : Load(folder, threads));
    }

    private (InferenceSession, string[])? Load(string folder, int threads)
    {
        string? path = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.onnx").Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (path is null)
        {
            LogModelMissing(_logger, folder);
            return null;
        }

        SessionOptions sessionOptions = new()
        {
            IntraOpNumThreads = threads,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // tanpa arena/memory pattern: memori dikembalikan setelah inferensi, bukan ditahan sebesar puncak
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
        };
        InferenceSession session = new(path, sessionOptions);
        // indeks 0 = blank CTC, lalu karakter dari metadata, lalu spasi (sama dengan PaddleOCR use_space_char)
        string[] lines = session.ModelMetadata.CustomMetadataMap["character"].Split('\n');
        string[] characters = ["", .. lines.Take(lines[^1].Length == 0 ? lines.Length - 1 : lines.Length).Select(c => c.TrimEnd('\r')), " "];
        return (session, characters);
    }

    public Task<IReadOnlyList<OcrText>> RecognizeAsync(
        VisionImage image, IReadOnlyList<TextLineRegion> regions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regions);
        OcrText[] result = [.. regions.Select(r => new OcrText(string.Empty, 0, r.Box))];
        if (regions.Count == 0 || _model.Value is not (InferenceSession session, string[] characters))
        {
            return Task.FromResult<IReadOnlyList<OcrText>>(result);
        }

        OpenCvImage source = OpenCvImage.From(image);
        string inputName = session.InputMetadata.Keys.First();
        for (int start = 0; start < regions.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<(int Index, Mat Line)> batch = [];
            try
            {
                for (int i = start; i < Math.Min(regions.Count, start + BatchSize); i++)
                {
                    if (Cut(source, regions[i]) is Mat line)
                    {
                        batch.Add((i, line));
                    }
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                int width = Math.Clamp(batch.Max(b => ScaledWidth(b.Line)), MinBatchWidth, MaxBatchWidth);
                float[] input = new float[batch.Count * 3 * LineHeight * width];
                for (int b = 0; b < batch.Count; b++)
                {
                    CopyNormalized(batch[b].Line, width, input, b * 3 * LineHeight * width);
                }

                DenseTensor<float> tensor = new(input, [batch.Count, 3, LineHeight, width]);
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                    session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
                Tensor<float> probs = outputs[0].AsTensor<float>();
                for (int b = 0; b < batch.Count; b++)
                {
                    (string text, double confidence) = CtcDecode.Greedy(probs, b, characters);
                    result[batch[b].Index] = new OcrText(text, confidence, regions[batch[b].Index].Box);
                }
            }
            finally
            {
                foreach ((_, Mat line) in batch)
                {
                    line.Dispose();
                }
            }
        }

        return Task.FromResult<IReadOnlyList<OcrText>>(result);
    }

    /// <summary>Potongan area (diputar sesuai <see cref="TextLineRegion.Rotation"/>); null jika terlalu kecil.</summary>
    private static Mat? Cut(OpenCvImage source, TextLineRegion region)
    {
        using Mat? crop = source.Crop(region.Box);
        if (crop is null)
        {
            return null;
        }

        Mat line = new();
        switch (region.Rotation)
        {
            case TextLineRotation.Clockwise:
                Cv2.Rotate(crop, line, RotateFlags.Rotate90Clockwise);
                break;
            case TextLineRotation.CounterClockwise:
                Cv2.Rotate(crop, line, RotateFlags.Rotate90Counterclockwise);
                break;
            default:
                crop.CopyTo(line);
                break;
        }

        return line;
    }

    private static int ScaledWidth(Mat line) => (int)Math.Ceiling(LineHeight * (double)line.Width / line.Height);

    /// <summary>Resize ke tinggi <see cref="LineHeight"/>, normalisasi (x/255 − 0,5)/0,5, CHW, sisa lebar 0.</summary>
    private static void CopyNormalized(Mat line, int width, float[] target, int offset)
    {
        int resizedWidth = Math.Min(width, ScaledWidth(line));
        using Mat resized = new();
        Cv2.Resize(line, resized, new Size(resizedWidth, LineHeight), 0, 0, InterpolationFlags.Linear);
        Mat[] channels = Cv2.Split(resized);
        try
        {
            int plane = LineHeight * width;
            for (int c = 0; c < 3; c++)
            {
                using Mat asFloat = new();
                channels[c].ConvertTo(asFloat, MatType.CV_32FC1, 2.0 / 255, -1.0);
                asFloat.GetArray(out float[] data);
                for (int y = 0; y < LineHeight; y++)
                {
                    Array.Copy(data, y * resizedWidth, target, offset + (c * plane) + (y * width), resizedWidth);
                }
            }
        }
        finally
        {
            foreach (Mat channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_model.IsValueCreated && _model.Value is (InferenceSession session, _))
        {
            session.Dispose();
        }
    }

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "Model recognizer teks (.onnx) tidak ditemukan di folder {Folder}: kotak label dibaca Paddle. Restart setelah model disalin.")]
    private static partial void LogModelMissing(ILogger logger, string folder);
}

/// <summary>Decode CTC greedy untuk output <c>[N, T, C]</c> (indeks 0 = blank).</summary>
public static class CtcDecode
{
    /// <summary>Karakter berturut-turut yang sama digabung, blank dibuang; keyakinan = rata-rata peluang karakter terpilih.</summary>
    public static (string Text, double Confidence) Greedy(Tensor<float> probs, int item, IReadOnlyList<string> characters)
    {
        ArgumentNullException.ThrowIfNull(probs);
        ArgumentNullException.ThrowIfNull(characters);
        int steps = probs.Dimensions[1];
        int classes = probs.Dimensions[2];
        System.Text.StringBuilder text = new();
        double sum = 0;
        int count = 0;
        int previous = -1;
        for (int t = 0; t < steps; t++)
        {
            int best = 0;
            float bestScore = float.MinValue;
            for (int k = 0; k < classes; k++)
            {
                float score = probs[item, t, k];
                if (score > bestScore)
                {
                    bestScore = score;
                    best = k;
                }
            }

            if (best != previous && best != 0 && best < characters.Count)
            {
                text.Append(characters[best]);
                sum += bestScore;
                count++;
            }

            previous = best;
        }

        return (text.ToString(), count == 0 ? 0 : sum / count);
    }
}
