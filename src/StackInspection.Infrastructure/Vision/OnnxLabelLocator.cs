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
/// ukuran input model, BGR 0–255 NCHW. Model batch 1, jadi kardus diproses satu per satu (±ms per kardus).
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

    public OnnxLabelLocator(IOptions<VisionOptions> options, ILogger<OnnxLabelLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _confidence = options.Value.LabelLocatorConfidence;
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
        int size = session.InputMetadata[inputName].Dimensions[^1];
        List<IReadOnlyList<LabelBox>> result = new(cartons.Count);
        foreach (BoundingBox carton in cartons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(Locate(session, inputName, size, source, carton));
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyList<LabelBox>>>(result);
    }

    private List<LabelBox> Locate(InferenceSession session, string inputName, int size, OpenCvImage source, BoundingBox carton)
    {
        using Mat? crop = source.Crop(carton);
        if (crop is null)
        {
            return [];
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

        DenseTensor<float> tensor = new(OnnxCartonDetector.ToChwFloat(canvas, size), [1, 3, size, size]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        Tensor<float> output = results[0].AsTensor<float>();
        float[] values = output is DenseTensor<float> dense ? dense.Buffer.ToArray() : [.. output];

        // Koordinat potongan kecil → potongan asli (/scale) → foto (+ titik asal).
        return [.. YoloxPostprocessor
            .Decode(values, geometry.Ratio * scale, _confidence, IouThreshold, crop.Width, crop.Height)
            .Select(b => new LabelBox(
                new BoundingBox(b.Box.X1 + originX, b.Box.Y1 + originY, b.Box.X2 + originX, b.Box.Y2 + originY),
                b.Confidence))];
    }

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
}
