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

/// <summary>Mendeteksi kardus pada foto.</summary>
public interface ICartonDetector
{
    Task<IReadOnlyList<CartonBox>> DetectAsync(VisionImage image, CancellationToken cancellationToken);
}

/// <summary>Satu potongan teks hasil OCR.</summary>
public sealed record OcrText(string Text, double Confidence);

/// <summary>Membaca teks (label SKU) pada area tertentu dari foto resolusi asli.</summary>
public interface ISkuLabelReader
{
    Task<IReadOnlyList<OcrText>> ReadAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken);
}

/// <summary>Mencari teks pada area foto (dipakai untuk mendeteksi stempel aplikasi kamera).</summary>
public interface ITextSpotter
{
    Task<IReadOnlyList<OcrText>> SpotAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken);
}
