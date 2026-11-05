using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackInspection.Application.Abstractions;
using StackInspection.Application.CollectSku;
using StackInspection.Application.Configuration;
using StackInspection.Application.Inspectability;
using StackInspection.Domain;

namespace StackInspection.Application.Tests;

internal sealed class StubImage(int width, int height) : VisionImage(width, height);

internal sealed class StubDecoder(int width, int height) : IImageDecoder
{
    public VisionImage Decode(ReadOnlyMemory<byte> data) => new StubImage(width, height);
}

internal sealed class StubDetector(IReadOnlyList<CartonBox> cartons) : ICartonDetector
{
    public int Calls { get; private set; }

    public DetectionModel? LastModel { get; private set; }

    public Task<IReadOnlyList<CartonBox>> DetectAsync(VisionImage image, DetectionModel model, CancellationToken cancellationToken)
    {
        Calls++;
        LastModel = model;
        return Task.FromResult(cartons);
    }
}

internal sealed class StubCatalog : IModelCatalog
{
    private readonly DetectionModel[] _models =
    [
        new("carton-v1", "a.onnx", 960, 0.5, 0.5, "candidate", "yolox-tiny", "ds-v1", null, null),
        new("carton-v2", "b.onnx", 960, 0.4, 0.5, "approved", "carton-v1", "ds-v2", null, null),
    ];

    public string DefaultModelName => "carton-v1";

    public IReadOnlyList<DetectionModel> List() => _models;

    public DetectionModel Resolve(string? name) =>
        _models.FirstOrDefault(m => m.Name == (string.IsNullOrWhiteSpace(name) ? DefaultModelName : name))
        ?? throw new Exceptions.RequestValidationException("model", "tidak ditemukan");
}

/// <summary>
/// Teks per kardus: <c>label</c> diletakkan di area label, <c>box</c> di bagian bawah kardus
/// (di luar area label).
/// </summary>
internal sealed class StubReader(Dictionary<BoundingBox, (string? Label, string? Box)> texts) : ISkuLabelReader
{
    public int Calls { get; private set; }

    public IReadOnlyList<BoundingBox> LastRegions { get; private set; } = [];

    /// <summary>minAreaWidth setiap panggilan (0 = resolusi asli).</summary>
    public List<int> Scans { get; } = [];

    /// <summary>
    /// Teks yang hanya terbaca jika diperbesar minimal <c>MinWidth</c> (angka kecil), posisinya seperti label.
    /// </summary>
    public Dictionary<BoundingBox, (string Text, int MinWidth)> EnlargedOnly { get; } = [];

    /// <summary>Teks di posisi bebas (koordinat foto), mis. label di kanan-bawah kardus.</summary>
    public List<OcrText> Anywhere { get; } = [];

    public Task<IReadOnlyList<OcrText>> ReadAllAsync(
        VisionImage image, IReadOnlyList<BoundingBox> regions, int minAreaWidth, CancellationToken cancellationToken)
    {
        Calls++;
        LastRegions = regions;
        Scans.Add(minAreaWidth);
        List<OcrText> result = [];
        foreach ((BoundingBox box, (string? label, string? whole)) in texts)
        {
            if (label is not null)
            {
                result.Add(new OcrText(label, 0.97, new BoundingBox(box.X1 + 10, box.Y1 + 10, box.X1 + 110, box.Y1 + 40)));
            }

            if (whole is not null)
            {
                result.Add(new OcrText(whole, 0.9, new BoundingBox(box.X1 + 50, box.Y2 - 60, box.X2 - 50, box.Y2 - 20)));
            }
        }

        result.AddRange(EnlargedOnly
            .Where(e => minAreaWidth > 0 && minAreaWidth >= e.Value.MinWidth)
            .Select(e => new OcrText(e.Value.Text, 0.9, new BoundingBox(e.Key.X1 + 10, e.Key.Y1 + 10, e.Key.X1 + 110, e.Key.Y1 + 40))));

        result.AddRange(Anywhere);

        // seperti OCR asli: hanya teks yang titik tengahnya di dalam area yang diminta
        return Task.FromResult<IReadOnlyList<OcrText>>([.. result.Where(t => t.Box is BoundingBox b && regions.Any(r =>
            b.CenterX >= r.X1 && b.CenterX <= r.X2 && b.CenterY >= r.Y1 && b.CenterY <= r.Y2))]);
    }
}

internal sealed class StubSpotter(params string[] texts) : ITextSpotter
{
    public Task<IReadOnlyList<OcrText>> SpotAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OcrText>>([.. texts.Select(t => new OcrText(t, 0.99))]);
}

/// <summary>Vektor tampilan per kotak dari fungsi; kotak yang tidak dikenal mendapat vektor nol.</summary>
internal sealed class StubEmbedder(Func<BoundingBox, float[]> vector) : ICartonEmbedder
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(VisionImage image, IReadOnlyList<BoundingBox> boxes, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<float[]>>([.. boxes.Select(vector)]);
    }
}

/// <summary>Label locator dengan kotak label tetap per kardus (dicocokkan dari kotak kardus).</summary>
internal sealed class StubLocator(Dictionary<BoundingBox, BoundingBox> labels) : ILabelLocator
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<IReadOnlyList<LabelBox>>> LocateAsync(
        VisionImage image, IReadOnlyList<BoundingBox> cartons, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<IReadOnlyList<LabelBox>>>([.. cartons.Select(c => labels.TryGetValue(c, out BoundingBox label)
            ? (IReadOnlyList<LabelBox>)[new LabelBox(label, 0.9)]
            : [])]);
    }
}

internal static class Build
{
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    public static CartonBox Carton(double x, double y, double w = 400, double h = 300) => new(new BoundingBox(x, y, x + w, y + h), 0.95);

    public static CollectSkuHandler Handler(
        IImageDecoder decoder,
        ICartonDetector detector,
        ISkuLabelReader reader,
        ITextSpotter? spotter = null,
        int maxFileMb = 10,
        bool cameraOverlay = false,
        int minCartonCount = 0,
        double minCartonCoverage = 0,
        ICartonEmbedder? embedder = null,
        bool ocrFullCartonFallback = false,
        IReadOnlyList<OcrScanOptions>? ocrRetryScans = null,
        double majorityMaxConfidence = 1.0,
        ILabelLocator? locator = null)
    {
        // gate fast reject dimatikan secara default supaya grid kecil di test lain tidak tertolak
        IOptions<PhotoOptions> photo = Options.Create(new PhotoOptions
        {
            CameraOverlayEnabled = cameraOverlay,
            MinCartonCount = minCartonCount,
            MinCartonCoverage = minCartonCoverage,
        });
        PhotoInspectabilityService inspectability = new(
        [
            new CameraOverlayCheck(spotter ?? new StubSpotter(), photo),
            new ResolutionCheck(photo),
        ]);
        return new CollectSkuHandler(
            decoder,
            detector,
            new StubCatalog(),
            reader,
            embedder ?? new StubEmbedder(_ => []),
            locator ?? new StubLocator([]),
            inspectability,
            Options.Create(new VisionOptions
            {
                AppearanceModel = embedder is null ? string.Empty : "carton-embed-v1",
                LabelLocatorModel = locator is null ? string.Empty : "label-loc-v1",
                OcrFullCartonFallback = ocrFullCartonFallback,
                OcrRetryScans = [.. ocrRetryScans ?? []],
                MajorityMaxConfidence = majorityMaxConfidence,
            }),
            Options.Create(new FrontLayerOptions()),
            Options.Create(new UploadOptions { MaxFileSizeMb = maxFileMb }),
            photo,
            NullLogger<CollectSkuHandler>.Instance);
    }
}
