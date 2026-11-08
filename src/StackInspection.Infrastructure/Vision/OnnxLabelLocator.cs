using System.Diagnostics;
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
/// Label locator YOLOX-nano 1 kelas (<c>label-loc-v*</c>, <c>label_locator_data.py</c> Carton Trainer, ONNX Runtime CPU).
/// Model: <c>&lt;Vision:ModelsDirectory&gt;/&lt;Vision:LabelLocatorModel&gt;/*.onnx</c>, session dibuat saat pertama kali
/// dipakai. Preprocess harus sama dengan data training: potong kotak kardus dari foto asli, perkecil (INTER_AREA)
/// sampai sisi terpanjang <see cref="CropMaxSide"/>, lalu letterbox YOLOX (INTER_LINEAR, kiri-atas, isi 114) ke
/// ukuran input model, BGR 0–255 NCHW. Model batch-1 diproses satu per satu; model dengan batch dinamis memakai
/// <see cref="VisionOptions.LabelLocatorBatchSize"/> crop per inferensi.
/// Model tidak ditemukan → warning di log dan tanpa kotak label (OCR kembali ke area label tetap).
/// </summary>
public sealed partial class OnnxLabelLocator : ILabelLocator, IDisposable
{
    /// <summary>Sisi terpanjang potongan kardus di data training (<c>build --max-side</c>).</summary>
    public const int CropMaxSide = 384;

    private const double IouThreshold = 0.5;

    private readonly Lazy<InferenceSession?> _session;
    private readonly ILogger<OnnxLabelLocator> _logger;
    private readonly double _confidence;
    private readonly int _batchSize;

    public OnnxLabelLocator(IOptions<VisionOptions> options, ILogger<OnnxLabelLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _confidence = options.Value.LabelLocatorConfidence;
        _batchSize = options.Value.LabelLocatorBatchSize;
        string name = options.Value.LabelLocatorModel;
        string folder = Path.Combine(OnnxCartonDetector.ResolvePath(options.Value.ModelsDirectory), name);
        int threads = options.Value.Threads;
        _session = new Lazy<InferenceSession?>(() => string.IsNullOrWhiteSpace(name) ? null : CreateSession(folder, threads));
    }

    private InferenceSession? CreateSession(string folder, int threads)
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
        return new InferenceSession(path, sessionOptions);
    }

    public Task<IReadOnlyList<IReadOnlyList<LabelBox>>> LocateAsync(
        VisionImage image, IReadOnlyList<BoundingBox> cartons, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cartons);
        OpenCvImage source = OpenCvImage.From(image);
        if (cartons.Count == 0 || _session.Value is not InferenceSession session)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyList<LabelBox>>>([.. cartons.Select(_ => (IReadOnlyList<LabelBox>)[])]);
        }

        string inputName = session.InputMetadata.Keys.First();
        int[] dimensions = session.InputMetadata[inputName].Dimensions;
        int size = dimensions[^1];
        int modelBatchSize = dimensions[0];
        if (modelBatchSize > 1)
        {
            throw new InvalidOperationException(
                $"Model label locator memakai batch statis {modelBatchSize}; gunakan batch 1 atau model dengan batch dinamis.");
        }

        // Model lama hasil ekspor batch-1 tetap berjalan; batch hanya aktif pada ONNX dengan dimensi pertama dinamis.
        int batchSize = modelBatchSize == 1 ? 1 : _batchSize;
        List<IReadOnlyList<LabelBox>> result = [.. cartons.Select(_ => (IReadOnlyList<LabelBox>)[])];
        List<(int Index, PreparedCrop Crop)> pending = new(batchSize);
        Stopwatch stopwatch = Stopwatch.StartNew();
        int inferenceRuns = 0;
        for (int i = 0; i < cartons.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparedCrop? crop = Prepare(source, cartons[i], size);
            if (crop is null)
            {
                continue;
            }

            pending.Add((i, crop));
            if (pending.Count == batchSize)
            {
                RunBatch(session, inputName, size, pending, result);
                pending.Clear();
                inferenceRuns++;
            }
        }

        if (pending.Count > 0)
        {
            RunBatch(session, inputName, size, pending, result);
            inferenceRuns++;
        }

        LogBatching(_logger, cartons.Count, batchSize, inferenceRuns, stopwatch.ElapsedMilliseconds);
        return Task.FromResult<IReadOnlyList<IReadOnlyList<LabelBox>>>(result);
    }

    private static PreparedCrop? Prepare(OpenCvImage source, BoundingBox carton, int size)
    {
        using Mat? crop = source.Crop(carton);
        if (crop is null)
        {
            return null;
        }

        // Titik asal potongan sama dengan OpenCvImage.Crop (floor + clamp).
        double originX = Math.Clamp(Math.Floor(carton.X1), 0, source.Width);
        double originY = Math.Clamp(Math.Floor(carton.Y1), 0, source.Height);
        double scale = Math.Min(1.0, (double)CropMaxSide / Math.Max(crop.Width, crop.Height));
        using Mat small = new();
        if (scale < 1.0)
        {
            Cv2.Resize(
                crop, small, new Size((int)Math.Round(crop.Width * scale), (int)Math.Round(crop.Height * scale)), 0, 0, InterpolationFlags.Area);
        }
        else
        {
            crop.CopyTo(small);
        }

        LetterboxGeometry geometry = LetterboxGeometry.Compute(small.Width, small.Height, size);
        using Mat resized = new();
        Cv2.Resize(small, resized, new Size(geometry.ResizedWidth, geometry.ResizedHeight), 0, 0, InterpolationFlags.Linear);
        using Mat canvas = new(new Size(size, size), MatType.CV_8UC3, Scalar.All(LetterboxGeometry.PadValue));
        using (Mat target = new(canvas, new Rect(0, 0, geometry.ResizedWidth, geometry.ResizedHeight)))
        {
            resized.CopyTo(target);
        }

        return new PreparedCrop(
            OnnxCartonDetector.ToChwFloat(canvas, size),
            crop.Width,
            crop.Height,
            originX,
            originY,
            scale,
            geometry.Ratio);
    }

    private void RunBatch(
        InferenceSession session,
        string inputName,
        int size,
        IReadOnlyList<(int Index, PreparedCrop Crop)> batch,
        List<IReadOnlyList<LabelBox>> result)
    {
        int itemLength = 3 * size * size;
        float[] input;
        if (batch.Count == 1)
        {
            input = batch[0].Crop.Input;
        }
        else
        {
            input = new float[batch.Count * itemLength];
            for (int i = 0; i < batch.Count; i++)
            {
                Array.Copy(batch[i].Crop.Input, 0, input, i * itemLength, itemLength);
            }
        }

        DenseTensor<float> tensor = new(input, [batch.Count, 3, size, size]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        Tensor<float> output = results[0].AsTensor<float>();
        if (output.Dimensions.Length != 3
            || output.Dimensions[0] != batch.Count
            || output.Dimensions[2] != YoloxPostprocessor.Channels)
        {
            throw new InvalidOperationException(
                $"Dimensi output model label locator tidak cocok dengan batch {batch.Count} dan {YoloxPostprocessor.Channels} channel.");
        }

        float[] values = output is DenseTensor<float> dense ? dense.Buffer.ToArray() : [.. output];
        int outputLength = values.Length / batch.Count;
        for (int i = 0; i < batch.Count; i++)
        {
            PreparedCrop crop = batch[i].Crop;
            // Koordinat potongan kecil → potongan asli (/scale) → foto (+ titik asal).
            result[batch[i].Index] = [.. YoloxPostprocessor
                .Decode(
                    values.AsSpan(i * outputLength, outputLength),
                    crop.Ratio * crop.Scale,
                    _confidence,
                    IouThreshold,
                    crop.Width,
                    crop.Height)
                .Select(b => new LabelBox(
                    new BoundingBox(
                        b.Box.X1 + crop.OriginX,
                        b.Box.Y1 + crop.OriginY,
                        b.Box.X2 + crop.OriginX,
                        b.Box.Y2 + crop.OriginY),
                    b.Confidence))];
        }
    }

    private sealed record PreparedCrop(
        float[] Input,
        int Width,
        int Height,
        double OriginX,
        double OriginY,
        double Scale,
        double Ratio);

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value?.Dispose();
        }
    }

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "Model label locator (.onnx) tidak ditemukan di folder {Folder}: OCR memakai area label tetap. Restart setelah model disalin.")]
    private static partial void LogModelMissing(ILogger logger, string folder);

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Label locator: {CartonCount} kardus, batch {BatchSize}, {InferenceRuns} inferensi, {ElapsedMs} ms")]
    private static partial void LogBatching(ILogger logger, int cartonCount, int batchSize, int inferenceRuns, long elapsedMs);
}
