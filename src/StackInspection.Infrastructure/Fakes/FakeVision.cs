using System.Text.Json;
using Microsoft.Extensions.Options;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Domain;

namespace StackInspection.Infrastructure.Fakes;

/// <summary>Satu kardus di fixture fake vision.</summary>
public sealed class FakeCarton
{
    /// <summary>[x1, y1, x2, y2] dalam piksel.</summary>
    public IReadOnlyList<double> Box { get; init; } = [];

    public double Confidence { get; init; } = 0.95;

    /// <summary>Teks OCR di area label; null = tidak ada teks.</summary>
    public string? LabelText { get; init; }

    public double LabelConfidence { get; init; } = 0.98;

    /// <summary>Teks OCR seluruh kardus (fallback); null = tidak ada teks.</summary>
    public string? BoxText { get; init; }

    public BoundingBox ToBox() => new(Box[0], Box[1], Box[2], Box[3]);
}

/// <summary>Fixture fake vision (JSON).</summary>
public sealed class FakeVisionFixture
{
    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    /// <summary>Teks yang "terbaca" di strip atas/bawah (untuk uji stempel kamera).</summary>
    public IReadOnlyList<string> OverlayTexts { get; init; } = [];

    public IReadOnlyList<FakeCarton> Cartons { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static FakeVisionFixture Load(string path)
    {
        string full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        using FileStream stream = File.OpenRead(full);
        return JsonSerializer.Deserialize<FakeVisionFixture>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Fixture fake vision kosong: {full}");
    }
}

/// <summary>Menyediakan fixture yang dimuat sekali dari <c>Vision:FakeFixturePath</c>.</summary>
public sealed class FakeVisionFixtureProvider
{
    private readonly Lazy<FakeVisionFixture> _fixture;

    public FakeVisionFixtureProvider(IOptions<VisionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string path = options.Value.FakeFixturePath;
        _fixture = new Lazy<FakeVisionFixture>(() => FakeVisionFixture.Load(path));
    }

    public FakeVisionFixture Fixture => _fixture.Value;
}

internal sealed class FakeVisionImage : VisionImage
{
    public FakeVisionImage(int width, int height)
        : base(width, height)
    {
    }
}

/// <summary>Mengembalikan ukuran gambar dari fixture (isi file hanya dicek formatnya oleh handler).</summary>
public sealed class FakeImageDecoder(FakeVisionFixtureProvider provider) : IImageDecoder
{
    public VisionImage Decode(ReadOnlyMemory<byte> data) =>
        new FakeVisionImage(provider.Fixture.ImageWidth, provider.Fixture.ImageHeight);
}

/// <summary>Mengembalikan kardus dari fixture (model apa pun).</summary>
public sealed class FakeCartonDetector(FakeVisionFixtureProvider provider) : ICartonDetector
{
    public Task<IReadOnlyList<CartonBox>> DetectAsync(VisionImage image, DetectionModel model, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CartonBox>>([.. provider.Fixture.Cartons.Select(c => new CartonBox(c.ToBox(), c.Confidence))]);
}

/// <summary>
/// Mengembalikan teks setiap kardus fixture beserta posisinya: <see cref="FakeCarton.LabelText"/> di pojok
/// kiri atas (area label), <see cref="FakeCarton.BoxText"/> di bagian bawah kardus.
/// </summary>
public sealed class FakeSkuLabelReader(FakeVisionFixtureProvider provider) : ISkuLabelReader
{
    public Task<IReadOnlyList<OcrText>> ReadAllAsync(VisionImage image, IReadOnlyList<BoundingBox> regions, CancellationToken cancellationToken)
    {
        List<OcrText> texts = [];
        foreach (FakeCarton carton in provider.Fixture.Cartons)
        {
            BoundingBox box = carton.ToBox();
            if (carton.LabelText is not null)
            {
                texts.Add(new OcrText(carton.LabelText, carton.LabelConfidence, Part(box, 0.05, 0.05, 0.4, 0.15)));
            }

            if (carton.BoxText is not null)
            {
                texts.Add(new OcrText(carton.BoxText, carton.LabelConfidence, Part(box, 0.2, 0.7, 0.8, 0.8)));
            }
        }

        return Task.FromResult<IReadOnlyList<OcrText>>(texts);
    }

    private static BoundingBox Part(BoundingBox box, double x1, double y1, double x2, double y2) =>
        new(box.X1 + (box.Width * x1), box.Y1 + (box.Height * y1), box.X1 + (box.Width * x2), box.Y1 + (box.Height * y2));
}

/// <summary>Mengembalikan <see cref="FakeVisionFixture.OverlayTexts"/> untuk strip mana pun.</summary>
public sealed class FakeTextSpotter(FakeVisionFixtureProvider provider) : ITextSpotter
{
    public Task<IReadOnlyList<OcrText>> SpotAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OcrText>>([.. provider.Fixture.OverlayTexts.Select(t => new OcrText(t, 0.99))]);
}

/// <summary>Katalog berisi satu model palsu (dan model default dari konfigurasi).</summary>
public sealed class FakeModelCatalog : IModelCatalog
{
    public const string FakeModelName = "fake-model";

    private readonly IReadOnlyList<DetectionModel> _models;

    public FakeModelCatalog(IOptions<VisionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VisionOptions vision = options.Value;
        DefaultModelName = FakeModelName;
        _models =
        [
            new DetectionModel(FakeModelName, "fake.onnx", vision.InputSize, vision.ConfidenceThreshold, vision.IouThreshold,
                "candidate", "yolox-tiny", "ds-fake", null, new ModelMetrics(0.9, 0.8, 0.85, 0.6)),
        ];
    }

    public string DefaultModelName { get; }

    public IReadOnlyList<DetectionModel> List() => _models;

    public DetectionModel Resolve(string? name)
    {
        string wanted = string.IsNullOrWhiteSpace(name) ? DefaultModelName : name.Trim();
        return _models.FirstOrDefault(m => m.Name == wanted)
            ?? throw new Application.Exceptions.RequestValidationException("model", $"Model '{wanted}' tidak ditemukan. Model tersedia: {FakeModelName}.");
    }
}
