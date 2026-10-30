using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Domain;

namespace StackInspection.Infrastructure.Vision;

/// <summary>
/// Embedder tampilan kardus (MobileNetV3 ImageNet hasil <c>carton_embedder.py</c> Carton Trainer, ONNX Runtime CPU).
/// Model: <c>&lt;Vision:ModelsDirectory&gt;/&lt;Vision:AppearanceModel&gt;/*.onnx</c>, session dibuat saat pertama
/// kali dipakai. Preprocess harus sama dengan export: crop kotak dari foto asli, resize langsung (tanpa letterbox,
/// INTER_AREA) ke <see cref="InputSize"/>, BGR → RGB, /255, NCHW; normalisasi ImageNet + L2 ada di dalam model.
/// Semua kardus satu foto diproses dalam satu batch.
/// </summary>
public sealed class OnnxCartonEmbedder : ICartonEmbedder, IDisposable
{
    public const int InputSize = 192;

    private readonly Lazy<InferenceSession> _session;

    public OnnxCartonEmbedder(IOptions<VisionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string folder = Path.Combine(OnnxCartonDetector.ResolvePath(options.Value.ModelsDirectory), options.Value.AppearanceModel);
        int threads = options.Value.Threads;
        _session = new Lazy<InferenceSession>(() => CreateSession(folder, threads));
    }

    private static InferenceSession CreateSession(string folder, int threads)
    {
        string? path = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.onnx").Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (path is null)
        {
            throw new FileNotFoundException($"Model embedder (.onnx) tidak ditemukan di folder: {folder}.", folder);
        }

        SessionOptions sessionOptions = new()
        {
            IntraOpNumThreads = threads,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        return new InferenceSession(path, sessionOptions);
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(VisionImage image, IReadOnlyList<BoundingBox> boxes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (boxes.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<float[]>>([]);
        }

        OpenCvImage source = OpenCvImage.From(image);
        InferenceSession session = _session.Value;
        const int plane = InputSize * InputSize;
        float[] input = new float[boxes.Count * 3 * plane];
        for (int i = 0; i < boxes.Count; i++)
        {
            using Mat? crop = source.Crop(boxes[i]);
            if (crop is null)
            {
                continue; // kotak terlalu kecil: input hitam
            }

            using Mat resized = new();
            Cv2.Resize(crop, resized, new Size(InputSize, InputSize), 0, 0, InterpolationFlags.Area);
            using Mat rgb = new();
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
            CopyChwUnit(rgb, input, i * 3 * plane);
        }

        DenseTensor<float> tensor = new(input, [boxes.Count, 3, InputSize, InputSize]);
        cancellationToken.ThrowIfCancellationRequested();
        string inputName = session.InputMetadata.Keys.First();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        Tensor<float> output = results[0].AsTensor<float>();
        int dimension = output.Dimensions[1];
        float[] values = output is DenseTensor<float> dense ? dense.Buffer.ToArray() : [.. output];
        float[][] vectors = [.. Enumerable.Range(0, boxes.Count).Select(i => values.AsSpan(i * dimension, dimension).ToArray())];
        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    /// <summary>HWC uint8 RGB → CHW float32 skala 0–1, ditulis ke <paramref name="target"/> mulai <paramref name="offset"/>.</summary>
    private static void CopyChwUnit(Mat rgb, float[] target, int offset)
    {
        const int plane = InputSize * InputSize;
        Mat[] channels = Cv2.Split(rgb);
        try
        {
            for (int c = 0; c < 3; c++)
            {
                using Mat asFloat = new();
                channels[c].ConvertTo(asFloat, MatType.CV_32FC1, 1.0 / 255);
                asFloat.GetArray(out float[] data);
                Array.Copy(data, 0, target, offset + (c * plane), plane);
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
        if (_session.IsValueCreated)
        {
            _session.Value.Dispose();
        }
    }
}
