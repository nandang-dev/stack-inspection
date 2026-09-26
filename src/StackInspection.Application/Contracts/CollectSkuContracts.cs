using StackInspection.Domain;

namespace StackInspection.Application.Contracts;

/// <summary>Kotak kardus dalam piksel foto asli (setelah orientasi EXIF diterapkan).</summary>
public sealed class BoundingBoxDto
{
    /// <summary>Koordinat X kiri.</summary>
    public int X1 { get; init; }

    /// <summary>Koordinat Y atas.</summary>
    public int Y1 { get; init; }

    /// <summary>Koordinat X kanan.</summary>
    public int X2 { get; init; }

    /// <summary>Koordinat Y bawah.</summary>
    public int Y2 { get; init; }
}

/// <summary>Satu kardus di lapisan depan beserta posisi dan hasil baca SKU.</summary>
public sealed class CellDto
{
    /// <summary>Baris foto, 0 = paling atas.</summary>
    public int Row { get; init; }

    /// <summary>Kolom, 0 = paling kiri. Satu kolom = satu stack.</summary>
    public int Column { get; init; }

    /// <summary>Posisi dalam kolom, 0 = kardus paling bawah.</summary>
    public int Level { get; init; }

    /// <summary>Kode SKU 8 digit, atau <c>UNKNOWN</c> / <c>LABEL_NOT_VISIBLE</c>.</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>Teks mentah hasil OCR; <c>null</c> jika label tidak terlihat.</summary>
    public string? OcrText { get; init; }

    /// <summary>Keyakinan OCR 0–1.</summary>
    public double OcrConfidence { get; init; }

    /// <summary>Keyakinan deteksi kardus 0–1.</summary>
    public double DetectionConfidence { get; init; }

    /// <summary>Status baca: <c>Matched</c>, <c>Corrected</c>, <c>Unknown</c>, <c>LabelNotVisible</c>.</summary>
    public ReadStatus ReadStatus { get; init; }

    /// <summary>Kotak kardus.</summary>
    public BoundingBoxDto BoundingBox { get; init; } = new();
}

/// <summary>Ringkasan satu kolom tumpukan.</summary>
public sealed class StackDto
{
    /// <summary>Indeks kolom.</summary>
    public int Column { get; init; }

    /// <summary>Jumlah kardus di kolom.</summary>
    public int Height { get; init; }

    /// <summary>SKU dari kardus paling bawah ke paling atas.</summary>
    public IReadOnlyList<string> SkusBottomToTop { get; init; } = [];
}

/// <summary>Jumlah kardus per SKU.</summary>
public sealed class DistinctSkuDto
{
    /// <summary>Kode SKU (termasuk <c>UNKNOWN</c> / <c>LABEL_NOT_VISIBLE</c> jika ada).</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>Jumlah kardus.</summary>
    public int Count { get; init; }
}

/// <summary>Hasil endpoint Collect SKU.</summary>
public sealed class CollectSkuResponse
{
    /// <summary>ID inspeksi (dibuat server, dipakai lagi di Analyze).</summary>
    public Guid InspectionId { get; init; }

    /// <summary>Lebar foto (piksel).</summary>
    public int ImageWidth { get; init; }

    /// <summary>Tinggi foto (piksel).</summary>
    public int ImageHeight { get; init; }

    /// <summary>Jumlah baris.</summary>
    public int RowCount { get; init; }

    /// <summary>Jumlah kolom.</summary>
    public int ColumnCount { get; init; }

    /// <summary>Jumlah kotak yang dibuang karena dianggap lapisan belakang.</summary>
    public int ExcludedBackLayerCount { get; init; }

    /// <summary>Semua kardus lapisan depan.</summary>
    public IReadOnlyList<CellDto> Cells { get; init; } = [];

    /// <summary>Ringkasan per kolom.</summary>
    public IReadOnlyList<StackDto> Stacks { get; init; } = [];

    /// <summary>Jumlah per SKU, urut <c>count</c> menurun lalu <c>sku</c> menaik.</summary>
    public IReadOnlyList<DistinctSkuDto> DistinctSkus { get; init; } = [];

    /// <summary>
    /// Kode peringatan: <c>LOW_DETECTION_CONFIDENCE</c>, <c>UNKNOWN_SKU_PRESENT</c>,
    /// <c>LABEL_NOT_VISIBLE_PRESENT</c>, <c>IRREGULAR_GRID</c>, <c>BACK_LAYER_EXCLUDED</c>.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Waktu proses (ms).</summary>
    public long ProcessingTimeMs { get; init; }
}

/// <summary>Kode peringatan Collect SKU.</summary>
public static class CollectWarnings
{
    /// <summary>Ada kardus dengan keyakinan deteksi rendah.</summary>
    public const string LowDetectionConfidence = "LOW_DETECTION_CONFIDENCE";

    /// <summary>Ada SKU yang tidak bisa dipastikan.</summary>
    public const string UnknownSkuPresent = "UNKNOWN_SKU_PRESENT";

    /// <summary>Ada kardus yang labelnya tidak terlihat.</summary>
    public const string LabelNotVisiblePresent = "LABEL_NOT_VISIBLE_PRESENT";

    /// <summary>Susunan tidak beraturan, baris/kolom dihitung sebisanya.</summary>
    public const string IrregularGrid = "IRREGULAR_GRID";

    /// <summary>Ada kotak lapisan belakang yang dibuang.</summary>
    public const string BackLayerExcluded = "BACK_LAYER_EXCLUDED";
}
