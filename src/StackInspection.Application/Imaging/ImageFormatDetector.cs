namespace StackInspection.Application.Imaging;

/// <summary>Format gambar yang didukung.</summary>
public enum ImageFormat
{
    Unsupported,
    Jpeg,
    Png,
}

/// <summary>Mengenali format dari magic bytes (bukan dari nama file / content-type yang bisa salah).</summary>
public static class ImageFormatDetector
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        return data.StartsWith(PngSignature) ? ImageFormat.Png : ImageFormat.Unsupported;
    }
}
