using StackInspection.Domain;

namespace StackInspection.Application.Abstractions;

/// <summary>
/// Gambar yang sudah di-decode (orientasi EXIF sudah diterapkan). Implementasi Infrastructure
/// menyimpan data piksel sesuai library vision yang dipakai.
/// </summary>
public abstract class VisionImage : IDisposable
{
    protected VisionImage(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>Men-decode file gambar (JPG/PNG).</summary>
public interface IImageDecoder
{
    /// <exception cref="Exceptions.InvalidImageException">Gambar tidak bisa dibaca.</exception>
    VisionImage Decode(ReadOnlyMemory<byte> data);
}

/// <summary>Metrik evaluasi model di golden test set (dari model card Carton Trainer).</summary>
public sealed record ModelMetrics(double Precision, double Recall, double Map50, double Map50To95);

/// <summary>Satu model deteksi yang tersedia.</summary>
public sealed record DetectionModel(
    string Name,
    string OnnxPath,
    int InputSize,
    double ConfidenceThreshold,
    double IouThreshold,
    string? Status,
    string? BaseModel,
    string? DatasetVersion,
    DateTimeOffset? TrainedAt,
    ModelMetrics? Metrics);

/// <summary>Daftar model deteksi yang tersedia di server.</summary>
public interface IModelCatalog
{
    IReadOnlyList<DetectionModel> List();

    string DefaultModelName { get; }

    /// <summary>Model dengan nama tersebut, atau model default jika <paramref name="name"/> kosong.</summary>
    /// <exception cref="Exceptions.RequestValidationException">Nama model tidak dikenal.</exception>
    DetectionModel Resolve(string? name);
}

/// <summary>Mendeteksi kardus pada foto dengan model tertentu.</summary>
public interface ICartonDetector
{
    Task<IReadOnlyList<CartonBox>> DetectAsync(VisionImage image, DetectionModel model, CancellationToken cancellationToken);
}

/// <summary>Satu potongan teks hasil OCR. <paramref name="Box"/> = posisi teks di foto (null jika tidak diketahui).</summary>
public sealed record OcrText(string Text, double Confidence, BoundingBox? Box = null);

/// <summary>
/// Membaca semua teks di dalam area-area kardus dari foto resolusi asli dalam satu kali proses.
/// Setiap hasil wajib membawa <see cref="OcrText.Box"/> supaya bisa dicocokkan ke kardusnya.
/// </summary>
public interface ISkuLabelReader
{
    Task<IReadOnlyList<OcrText>> ReadAllAsync(VisionImage image, IReadOnlyList<BoundingBox> regions, CancellationToken cancellationToken);
}

/// <summary>Mencari teks pada area foto (dipakai untuk mendeteksi stempel aplikasi kamera).</summary>
public interface ITextSpotter
{
    Task<IReadOnlyList<OcrText>> SpotAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken);
}
