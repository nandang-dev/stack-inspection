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

/// <summary>
/// Satu sel grid tampilan yang ditempati kardus lapisan depan. Kardus besar/lebar menempati beberapa sel dan
/// muncul beberapa kali dengan isi dan <c>id</c> yang sama.
/// </summary>
public class CellDto
{
    /// <summary>
    /// ID kardus (0..N-1, urut dari kiri-atas). Entri dengan <c>id</c> sama = kardus yang sama.
    /// Selalu diisi oleh Collect SKU; <c>null</c> hanya pada hasil versi lama (ditolak Analyze).
    /// </summary>
    public int? Id { get; init; }

    /// <summary>Baris grid tampilan, 0 = paling atas.</summary>
    public int Row { get; init; }

    /// <summary>Kolom grid tampilan, 0 = paling kiri.</summary>
    public int Column { get; init; }

    /// <summary>Level fisik di tumpukan: 0 = di lantai, selain itu 1 + level tertinggi kardus di <c>restsOn</c>.</summary>
    public int Level { get; init; }

    /// <summary>
    /// <c>id</c> kardus yang langsung ditumpu (kosong = di lantai; lebih dari satu = kardus menjembatani beberapa
    /// tumpukan). <c>null</c> hanya pada hasil versi lama.
    /// </summary>
    public IReadOnlyList<int>? RestsOn { get; init; }

    /// <summary>Kode SKU 8 digit, atau <c>UNKNOWN</c> / <c>LABEL_NOT_VISIBLE</c>.</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>Teks mentah hasil OCR; <c>null</c> jika label tidak terlihat.</summary>
    public string? OcrText { get; init; }

    /// <summary>
    /// Keyakinan OCR 0–1 untuk teks label yang dipakai. Pada <c>InferredByAppearance</c> berisi kemiripan tampilan
    /// (cosine 0–1) ke kardus acuan terbaik, karena SKU-nya tidak dibaca dari label.
    /// </summary>
    public double OcrConfidence { get; init; }

    /// <summary>Keyakinan deteksi kardus 0–1.</summary>
    public double DetectionConfidence { get; init; }

    /// <summary>
    /// Status baca: <c>Matched</c>, <c>Corrected</c>, <c>Unknown</c>, <c>LabelNotVisible</c>,
    /// <c>InferredByAppearance</c> (SKU dari kardus lain yang tampilannya sangat mirip, bukan hasil OCR).
    /// </summary>
    public ReadStatus ReadStatus { get; init; }

    /// <summary>Kotak kardus.</summary>
    public BoundingBoxDto BoundingBox { get; init; } = new();
}

/// <summary>
/// Satu jalur tumpukan dari kardus di lantai sampai kardus teratas, mengikuti <c>restsOn</c>. Kardus yang
/// menjembatani beberapa tumpukan muncul di setiap jalur yang melewatinya.
/// </summary>
public sealed class StackDto
{
    /// <summary>Nomor jalur (0..), urut dari kiri.</summary>
    public int Path { get; init; }

    /// <summary>Kolom grid tampilan kardus paling bawah di jalur ini.</summary>
    public int Column { get; init; }

    /// <summary>Jumlah kardus di jalur.</summary>
    public int Height { get; init; }

    /// <summary>
    /// Kardus di jalur ini, urut dari paling bawah ke paling atas. Isinya sama dengan entri <c>cells</c> ber-id sama;
    /// kardus yang menempati beberapa sel diwakili sel kiri-atasnya.
    /// </summary>
    public IReadOnlyList<CellDto> Cartons { get; init; } = [];
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

    /// <summary>Nama model deteksi yang dipakai (lihat <c>GET /api/v1/models</c>).</summary>
    public string Model { get; init; } = string.Empty;

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

    /// <summary>
    /// Sel grid tampilan yang ditempati kardus lapisan depan, urut <c>row</c> lalu <c>column</c>. Jumlah kardus =
    /// jumlah <c>id</c> unik, bukan jumlah entri.
    /// </summary>
    public IReadOnlyList<CellDto> Cells { get; init; } = [];

    /// <summary>Jalur tumpukan bawah → atas (maks. 200; lebih dari itu muncul <c>STACK_PATHS_TRUNCATED</c>).</summary>
    public IReadOnlyList<StackDto> Stacks { get; init; } = [];

    /// <summary>Jumlah per SKU, urut <c>count</c> menurun lalu <c>sku</c> menaik.</summary>
    public IReadOnlyList<DistinctSkuDto> DistinctSkus { get; init; } = [];

    /// <summary>
    /// Kode peringatan: <c>LOW_DETECTION_CONFIDENCE</c>, <c>UNKNOWN_SKU_PRESENT</c>,
    /// <c>LABEL_NOT_VISIBLE_PRESENT</c>, <c>IRREGULAR_GRID</c>, <c>BACK_LAYER_EXCLUDED</c>,
    /// <c>SKU_CORRECTED_BY_MAJORITY</c>, <c>SKU_CORRECTED_BY_PARTIAL_READ</c>, <c>SKU_INFERRED_BY_APPEARANCE</c>,
    /// <c>STACK_PATHS_TRUNCATED</c>.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Waktu proses (ms).</summary>
    public long ProcessingTimeMs { get; init; }

    /// <summary>Rincian waktu proses per tahap (ms); jumlahnya ≈ <c>processingTimeMs</c>.</summary>
    public CollectSkuTimingsDto Timings { get; init; } = new();

    /// <summary>
    /// Log server selama memproses foto ini (OCR per tahap, recognizer, fast reject, inferensi tampilan, peringatan
    /// model tidak ditemukan, dll.), untuk analisis tanpa membuka log server. Hanya untuk diagnosis; isi dan formatnya
    /// bisa berubah, jangan diproses otomatis oleh client.
    /// </summary>
    public IReadOnlyList<ProcessingLogDto> Logs { get; init; } = [];
}

/// <summary>Satu baris log proses.</summary>
public sealed class ProcessingLogDto
{
    /// <summary>Waktu sejak proses dimulai (ms).</summary>
    public long AtMs { get; init; }

    /// <summary>Level log: <c>Information</c>, <c>Warning</c>, <c>Error</c>.</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>Komponen yang menulis log (mis. <c>CollectSkuHandler</c>, <c>PaddleOcrTextReader</c>).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Isi log.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>Waktu eksekusi tiap tahap pipeline Collect SKU (ms).</summary>
public sealed class CollectSkuTimingsDto
{
    /// <summary>Validasi upload + decode gambar (termasuk orientasi EXIF).</summary>
    public long DecodeMs { get; init; }

    /// <summary>Photo gate: resolusi, fast-reject classifier, cek stempel kamera.</summary>
    public long InspectMs { get; init; }

    /// <summary>Deteksi kardus + filter kotak bersarang, gate hasil deteksi, dan filter lapisan depan.</summary>
    public long DetectMs { get; init; }

    /// <summary>Label locator: mencari kotak label nomor SKU di setiap kardus (0 jika <c>Vision:LabelLocatorModel</c> kosong).</summary>
    public long LabelLocateMs { get; init; }

    /// <summary>
    /// OCR kotak label hasil label locator, lalu area label tetap untuk kardus yang belum terbaca (+ tahap 2 seluruh
    /// kardus jika <c>Vision:OcrFullCartonFallback</c> aktif), termasuk menunggu giliran jika OCR sedang dipakai
    /// request lain.
    /// </summary>
    public long OcrMs { get; init; }

    /// <summary>Pencocokan SKU: fuzzy match, koreksi mayoritas, potongan nomor, dan inferensi tampilan.</summary>
    public long SkuResolveMs { get; init; }

    /// <summary>Penyusunan grid, stack, dan ringkasan response.</summary>
    public long BuildMs { get; init; }
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

    /// <summary>
    /// Tanpa <c>candidateSkus</c>: ada hasil baca yang dikoreksi ke SKU mayoritas di foto (status <c>Corrected</c>).
    /// </summary>
    public const string SkuCorrectedByMajority = "SKU_CORRECTED_BY_MAJORITY";

    /// <summary>
    /// Ada nomor SKU yang hanya terbaca sebagian (label terpotong/tertutup) lalu dilengkapi dari SKU yang
    /// terbaca utuh di foto atau dari <c>candidateSkus</c> (status <c>Corrected</c>).
    /// </summary>
    public const string SkuCorrectedByPartialRead = "SKU_CORRECTED_BY_PARTIAL_READ";

    /// <summary>Ada kardus yang SKU-nya diambil dari kardus lain yang tampilannya sangat mirip (status <c>InferredByAppearance</c>).</summary>
    public const string SkuInferredByAppearance = "SKU_INFERRED_BY_APPEARANCE";

    /// <summary>Susunan sangat bercabang: <c>stacks</c> dipotong di batas jumlah jalur (penilaian Analyze tidak terpengaruh).</summary>
    public const string StackPathsTruncated = "STACK_PATHS_TRUNCATED";
}
