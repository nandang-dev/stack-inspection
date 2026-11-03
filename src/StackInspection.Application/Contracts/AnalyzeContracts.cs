namespace StackInspection.Application.Contracts;

/// <summary>Data master satu SKU.</summary>
public sealed class MasterSkuDto
{
    /// <summary>Kode SKU 8 digit.</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>Nama produk.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Jumlah maksimal kardus SKU yang sama dalam satu jalur tumpukan (harus &gt; 0).</summary>
    public int MaxStack { get; init; }

    /// <summary>Berat per kardus (kg), informatif.</summary>
    public double Weight { get; init; }

    /// <summary>Kelas berat: <c>A</c> (ringan), <c>B</c>, <c>C</c> (berat, harus di bawah).</summary>
    public string Class { get; init; } = string.Empty;
}

/// <summary>Request endpoint Analyze.</summary>
public sealed class AnalyzeStackRequest
{
    /// <summary>
    /// Body response Collect SKU apa adanya. Hanya <c>cells</c> dan <c>inspectionId</c> yang dipakai; hasil versi
    /// lama (cell tanpa <c>id</c>/<c>restsOn</c>) ditolak 400.
    /// </summary>
    public CollectSkuResponse? CollectResult { get; init; }

    /// <summary>Data master SKU.</summary>
    public IReadOnlyList<MasterSkuDto> MasterSkus { get; init; } = [];
}

/// <summary>Satu pelanggaran aturan.</summary>
public sealed class ViolationDto
{
    /// <summary>
    /// Jenis: <c>UNKNOWN_SKU</c>, <c>LABEL_NOT_VISIBLE</c>, <c>MAX_STACK_EXCEEDED</c>, <c>CLASS_POSITION_INVALID</c>.
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary><c>id</c> kardus yang melanggar (sorot semua sel <c>cells</c> dengan id ini).</summary>
    public int CartonId { get; init; }

    /// <summary>Baris grid tampilan (sel kiri-atas kardus).</summary>
    public int Row { get; init; }

    /// <summary>Kolom grid tampilan (sel kiri-atas kardus).</summary>
    public int Column { get; init; }

    /// <summary>Level fisik kardus di tumpukan.</summary>
    public int Level { get; init; }

    /// <summary>SKU kardus.</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>Pesan dalam bahasa Indonesia.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>Ringkasan per SKU.</summary>
public sealed class SkuSummaryDto
{
    /// <summary>Kode SKU.</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>Nama dari master; <c>null</c> jika SKU tidak ada di master.</summary>
    public string? Name { get; init; }

    /// <summary>Kelas dari master; <c>null</c> jika SKU tidak ada di master.</summary>
    public string? Class { get; init; }

    /// <summary>Jumlah kardus.</summary>
    public int Count { get; init; }

    /// <summary>Jumlah kardus SKU ini yang melanggar minimal satu aturan.</summary>
    public int ViolationCount { get; init; }
}

/// <summary>Ringkasan satu jalur tumpukan (bawah → atas mengikuti <c>restsOn</c>).</summary>
public sealed class StackSummaryDto
{
    /// <summary>Nomor jalur (0..), urut dari kiri.</summary>
    public int Path { get; init; }

    /// <summary>Kolom grid tampilan kardus paling bawah di jalur ini.</summary>
    public int Column { get; init; }

    /// <summary>Jumlah kardus di jalur.</summary>
    public int Height { get; init; }

    /// <summary>Kardus di jalur ini, urut dari paling bawah ke paling atas.</summary>
    public IReadOnlyList<StackSummaryCartonDto> Cartons { get; init; } = [];

    /// <summary>Jumlah kardus yang melanggar di jalur ini.</summary>
    public int ViolationCount { get; init; }
}

/// <summary>
/// Satu kardus di jalur tumpukan hasil Analyze: data kardus yang sama dengan <c>cells</c> Collect SKU (sel kiri-atas)
/// ditambah pelanggarannya.
/// </summary>
public sealed class StackSummaryCartonDto : CellDto
{
    /// <summary>Jenis pelanggaran kardus ini (kosong = tidak melanggar), urut menaik.</summary>
    public IReadOnlyList<string> Violations { get; init; } = [];
}

/// <summary>Hasil endpoint Analyze.</summary>
public sealed class AnalyzeStackResponse
{
    /// <summary>ID inspeksi dari Collect SKU.</summary>
    public Guid InspectionId { get; init; }

    /// <summary>Jumlah kardus (id unik) tanpa pelanggaran.</summary>
    public int TotalScore { get; init; }

    /// <summary>Jumlah kardus (id unik).</summary>
    public int MaxPossibleScore { get; init; }

    /// <summary><c>totalScore / maxPossibleScore × 100</c>, dibulatkan 2 desimal.</summary>
    public double PercentageScore { get; init; }

    /// <summary>Pelanggaran, urut <c>row</c>, <c>column</c>, lalu <c>type</c>.</summary>
    public IReadOnlyList<ViolationDto> Violations { get; init; } = [];

    /// <summary>Ringkasan per SKU.</summary>
    public IReadOnlyList<SkuSummaryDto> SkuSummary { get; init; } = [];

    /// <summary>Ringkasan per jalur tumpukan.</summary>
    public IReadOnlyList<StackSummaryDto> Stacks { get; init; } = [];
}

/// <summary>Kode jenis pelanggaran di response.</summary>
public static class ViolationCodes
{
    /// <summary>SKU tidak terbaca atau tidak ada di master.</summary>
    public const string UnknownSku = "UNKNOWN_SKU";

    /// <summary>Label SKU tidak terlihat.</summary>
    public const string LabelNotVisible = "LABEL_NOT_VISIBLE";

    /// <summary>Melebihi maxStack.</summary>
    public const string MaxStackExceeded = "MAX_STACK_EXCEEDED";

    /// <summary>Kelas berat tidak sesuai urutan.</summary>
    public const string ClassPositionInvalid = "CLASS_POSITION_INVALID";
}
