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

/// <summary>Mengembalikan kardus dari fixture.</summary>
public sealed class FakeCartonDetector(FakeVisionFixtureProvider provider) : ICartonDetector
{
    public Task<IReadOnlyList<CartonBox>> DetectAsync(VisionImage image, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CartonBox>>([.. provider.Fixture.Cartons.Select(c => new CartonBox(c.ToBox(), c.Confidence))]);
}

/// <summary>
/// Mengembalikan teks label kardus yang memuat pusat area yang diminta. Area lebih kecil dari
/// kardus = area label (<see cref="FakeCarton.LabelText"/>), selain itu seluruh kardus (<see cref="FakeCarton.BoxText"/>).
/// </summary>
public sealed class FakeSkuLabelReader(FakeVisionFixtureProvider provider) : ISkuLabelReader
{
    public Task<IReadOnlyList<OcrText>> ReadAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken)
    {
        FakeCarton? carton = provider.Fixture.Cartons.FirstOrDefault(c =>
        {
            BoundingBox box = c.ToBox();
            return region.X1 >= box.X1 - 1 && region.Y1 >= box.Y1 - 1 && region.X2 <= box.X2 + 1 && region.Y2 <= box.Y2 + 1;
        });
        if (carton is null)
        {
            return Task.FromResult<IReadOnlyList<OcrText>>([]);
        }

        bool isLabelArea = region.Height < carton.ToBox().Height * 0.9;
        string? text = isLabelArea ? carton.LabelText : carton.BoxText;
        return Task.FromResult<IReadOnlyList<OcrText>>(text is null ? [] : [new OcrText(text, carton.LabelConfidence)]);
    }
}

/// <summary>Mengembalikan <see cref="FakeVisionFixture.OverlayTexts"/> untuk strip mana pun.</summary>
public sealed class FakeTextSpotter(FakeVisionFixtureProvider provider) : ITextSpotter
{
    public Task<IReadOnlyList<OcrText>> SpotAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OcrText>>([.. provider.Fixture.OverlayTexts.Select(t => new OcrText(t, 0.99))]);
}
