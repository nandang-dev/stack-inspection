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

    public Task<IReadOnlyList<OcrText>> ReadAllAsync(VisionImage image, IReadOnlyList<BoundingBox> regions, CancellationToken cancellationToken)
    {
        Calls++;
        LastRegions = regions;
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

        return Task.FromResult<IReadOnlyList<OcrText>>(result);
    }
}

internal sealed class StubSpotter(params string[] texts) : ITextSpotter
{
    public Task<IReadOnlyList<OcrText>> SpotAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OcrText>>([.. texts.Select(t => new OcrText(t, 0.99))]);
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
        int maxFileMb = 10)
    {
        IOptions<PhotoOptions> photo = Options.Create(new PhotoOptions());
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
            inspectability,
            Options.Create(new VisionOptions()),
            Options.Create(new FrontLayerOptions()),
            Options.Create(new UploadOptions { MaxFileSizeMb = maxFileMb }),
            NullLogger<CollectSkuHandler>.Instance);
    }
}
