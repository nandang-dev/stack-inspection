namespace StackInspection.Domain;

/// <summary>Kelas berat produk. A paling ringan, C paling berat (harus di bawah).</summary>
public enum SkuClass
{
    A = 1,
    B = 2,
    C = 3,
}

/// <summary>Status pembacaan SKU dari label kardus.</summary>
public enum ReadStatus
{
    /// <summary>Teks OCR sama persis dengan candidate, atau tepat 8 digit jika tanpa candidate.</summary>
    Matched,

    /// <summary>Cocok ke candidate setelah fuzzy match.</summary>
    Corrected,

    /// <summary>Ada teks digit tetapi SKU tidak bisa dipastikan.</summary>
    Unknown,

    /// <summary>Tidak ada teks digit sama sekali (label tidak menghadap kamera).</summary>
    LabelNotVisible,
}

/// <summary>Kotak dalam koordinat piksel foto asli.</summary>
public readonly record struct BoundingBox(double X1, double Y1, double X2, double Y2)
{
    public double Width => X2 - X1;

    public double Height => Y2 - Y1;

    public double CenterX => (X1 + X2) / 2;

    public double CenterY => (Y1 + Y2) / 2;
}

/// <summary>Kardus hasil deteksi.</summary>
public sealed record CartonBox(BoundingBox Box, double Confidence);

/// <summary>Satu kardus beserta posisinya di grid dan hasil baca SKU.</summary>
public sealed record DetectedCell(
    int Row,
    int Column,
    int Level,
    string Sku,
    string? OcrText,
    double OcrConfidence,
    double DetectionConfidence,
    ReadStatus ReadStatus,
    BoundingBox Box);

/// <summary>Data master SKU yang dipakai aturan penilaian.</summary>
public sealed record MasterSku(string Sku, string Name, int MaxStack, double Weight, SkuClass Class);

/// <summary>Kode SKU khusus untuk sel yang tidak terbaca.</summary>
public static class SkuCodes
{
    public const string Unknown = "UNKNOWN";

    public const string LabelNotVisible = "LABEL_NOT_VISIBLE";
}
