using StackInspection.Domain;

namespace StackInspection.Application.Inspectability;

/// <summary>
/// Gate fast reject dari hasil deteksi (sebelum OCR): muatan terlalu sedikit atau terlalu kecil di foto
/// (bukan muatan, kontainer kosong, foto terlalu jauh). Murah karena memakai kotak yang sudah dihitung.
/// </summary>
public static class DetectionGate
{
    public const string TooFewCartons = "too-few-cartons";
    public const string LowCoverage = "low-coverage";

    /// <summary>Nama gate yang gagal, atau <c>null</c> jika lolos. Ambang 0 = gate nonaktif.</summary>
    public static string? Check(IReadOnlyList<CartonBox> cartons, int imageWidth, int imageHeight, int minCartons, double minCoverage)
    {
        ArgumentNullException.ThrowIfNull(cartons);
        if (cartons.Count < minCartons)
        {
            return TooFewCartons;
        }

        return minCoverage > 0 && Coverage(cartons, imageWidth, imageHeight) < minCoverage ? LowCoverage : null;
    }

    /// <summary>Total luas kotak / luas foto (maks. 1). Kotak bersarang sudah dibuang sebelumnya.</summary>
    public static double Coverage(IReadOnlyList<CartonBox> cartons, int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(cartons);
        double imageArea = (double)imageWidth * imageHeight;
        return imageArea <= 0 ? 0 : Math.Min(1, cartons.Sum(c => c.Box.Width * c.Box.Height) / imageArea);
    }
}
