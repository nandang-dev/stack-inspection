using System.ComponentModel.DataAnnotations;

namespace StackInspection.Application.Configuration;

/// <summary>Konfigurasi vision (section <c>Vision</c>).</summary>
public sealed class VisionOptions
{
    public const string SectionName = "Vision";

    /// <summary>true = pakai fake vision dari fixture JSON (unit/integration test).</summary>
    public bool UseFake { get; set; }

    /// <summary>File fixture untuk fake vision, relatif terhadap folder aplikasi.</summary>
    public string FakeFixturePath { get; set; } = "fixtures/reference-grid.json";

    /// <summary>
    /// Folder model deteksi di luar repo (absolut, atau relatif terhadap folder aplikasi). Setiap subfolder
    /// berisi satu model: <c>&lt;nama&gt;/&lt;nama&gt;.onnx</c> + <c>model-card.json</c> hasil export Carton Trainer.
    /// File model tidak di-commit.
    /// </summary>
    [Required]
    public string ModelsDirectory { get; set; } = "models";

    /// <summary>Model yang dipakai jika request tidak memilih model.</summary>
    [Required]
    public string DefaultModel { get; set; } = "carton-v1";

    /// <summary>Default jika model-card.json tidak mencantumkan inputSize.</summary>
    [Range(320, 1920)]
    public int InputSize { get; set; } = 960;

    /// <summary>Default jika model-card.json tidak mencantumkan recommendedThresholds.</summary>
    [Range(0.01, 0.99)]
    public double ConfidenceThreshold { get; set; } = 0.5;

    /// <summary>Default jika model-card.json tidak mencantumkan recommendedThresholds.</summary>
    [Range(0.01, 0.99)]
    public double IouThreshold { get; set; } = 0.5;

    [Range(1, 64)]
    public int Threads { get; set; } = 4;

    /// <summary>
    /// Jumlah Collect SKU/Preview yang boleh diproses bersamaan. Request berikutnya langsung ditolak 429
    /// <c>SERVER_BUSY</c> (tidak diantrekan) supaya RAM/CPU tidak habis oleh request yang menumpuk.
    /// </summary>
    [Range(1, 16)]
    public int MaxConcurrentInspections { get; set; } = 1;

    [Range(0.1, 1.0)]
    public double LabelCropWidthRatio { get; set; } = 0.6;

    [Range(0.1, 1.0)]
    public double LabelCropHeightRatio { get; set; } = 0.3;

    /// <summary>
    /// OCR tahap 2: kardus yang SKU-nya tidak terbaca dari area label (tahap 1) di-OCR ulang seluruh kardusnya.
    /// false (default) = hanya area label (<see cref="LabelCropWidthRatio"/> × <see cref="LabelCropHeightRatio"/>
    /// kiri-atas), jauh lebih cepat tetapi label di luar area itu tidak terbaca.
    /// </summary>
    public bool OcrFullCartonFallback { get; set; }

    /// <summary>Tumpang tindih tile OCR (px) untuk area yang lebih besar dari satu halaman deteksi 960 px.</summary>
    [Range(0, 480)]
    public int OcrTileOverlap { get; set; } = 320;

    /// <summary>
    /// OCR tahap 1b (baca ulang berbagai ukuran): setelah tahap 1 (resolusi asli), kardus yang belum terbaca dibaca
    /// ulang dengan setiap scan berurutan; scan berikutnya hanya untuk kardus yang masih belum terbaca. Kardus yang
    /// sudah terbaca tidak disentuh. Kosong = tahap 1b mati. Menambah halaman deteksi, bukan RAM.
    /// Isi default ada di appsettings.json (tidak di sini, agar binder konfigurasi tidak menggandakan daftar).
    /// </summary>
    public List<OcrScanOptions> OcrRetryScans { get; set; } = [];

    /// <summary>Batas perbesaran area OCR (lihat <see cref="OcrScanOptions.MinAreaWidth"/>).</summary>
    [Range(1.0, 4.0)]
    public double OcrMaxUpscale { get; set; } = 3.0;

    /// <summary>
    /// Skor minimal kotak teks hasil deteksi pada scan tahap 1b (tahap 1 memakai default PaddleOCR 0,7). Lebih rendah =
    /// teks samar/kecil ikut terbaca; teks palsu tetap tersaring karena harus cocok dengan pola SKU/candidateSkus.
    /// </summary>
    [Range(0.1, 0.95)]
    public double OcrBoxScoreThreshold { get; set; } = 0.5;

    [Range(0, 4)]
    public int FuzzyMaxDistance { get; set; } = 2;

    /// <summary>
    /// Koreksi mayoritas (hanya jika <c>candidateSkus</c> kosong): SKU yang terbaca minimal sekian kali di foto
    /// dianggap mayoritas. 0 = nonaktif.
    /// </summary>
    [Range(0, 100)]
    public int MajorityMinCount { get; set; } = 3;

    /// <summary>SKU mayoritas harus muncul minimal sekian kali lipat dari SKU yang dikoreksi.</summary>
    [Range(1, 100)]
    public int MajorityDominance { get; set; } = 3;

    /// <summary>
    /// Selisih digit maksimal agar hasil baca dikoreksi ke SKU mayoritas. Tuning case 01: salah baca OCR umumnya
    /// 1–2 digit (65590068 → 65590059, 64845163 → 64845107). Risiko yang diterima: varian produk asli yang beda
    /// ≤ 2 digit dan kalah jumlah (65081663 di antara 3× 65081657) ikut dikoreksi ke mayoritas.
    /// </summary>
    [Range(0, 4)]
    public int MajorityMaxDistance { get; set; } = 2;

    /// <summary>
    /// Hasil baca yang terbaca langsung (<c>Matched</c>) dengan keyakinan OCR ≥ nilai ini tidak dikoreksi mayoritas:
    /// varian produk asli yang labelnya jelas (65081663 di antara 65081657) dipertahankan. 1 = semua boleh dikoreksi.
    /// Kalibrasi dari nilai keyakinan yang ditampilkan di Preview.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MajorityMaxConfidence { get; set; } = 0.97;

    /// <summary>
    /// Koreksi mayoritas dengan dukungan tampilan: selisih digit sampai nilai ini (di atas
    /// <see cref="MajorityMaxDistance"/>) tetap dikoreksi jika tampilan kardus mirip ≥
    /// <see cref="AppearanceMinSimilarity"/> dengan kardus SKU mayoritas (mis. 15690069 → 65590059, beda 3 digit).
    /// Butuh <see cref="AppearanceModel"/>. 0 = nonaktif.
    /// </summary>
    [Range(0, 4)]
    public int MajorityAppearanceMaxDistance { get; set; } = 3;

    /// <summary>
    /// Potongan nomor SKU (label terpotong/tertutup) minimal sekian digit dilengkapi dari SKU yang terbaca utuh
    /// di foto atau dari <c>candidateSkus</c>. 0 = nonaktif.
    /// </summary>
    [Range(0, 7)]
    public int PartialReadMinDigits { get; set; } = 5;

    /// <summary>
    /// Nama subfolder model embedder tampilan kardus di <see cref="ModelsDirectory"/> (mis. <c>carton-embed-v1</c>).
    /// Kardus yang labelnya tidak terbaca diberi SKU kardus lain yang sangat mirip. Kosong = nonaktif.
    /// </summary>
    public string AppearanceModel { get; set; } = string.Empty;

    /// <summary>
    /// Nama subfolder model label locator di <see cref="ModelsDirectory"/> (mis. <c>label-loc-v1</c>, Carton Trainer).
    /// OCR tahap 0 membaca kotak label yang ditemukan model ini; kardus yang belum terbaca tetap dibaca dari area
    /// label tetap dan <see cref="OcrRetryScans"/>. Kosong = nonaktif (perilaku sama seperti sebelum ada locator).
    /// </summary>
    public string LabelLocatorModel { get; set; } = string.Empty;

    /// <summary>Skor minimal kotak label (objectness × skor kelas).</summary>
    [Range(0.05, 1.0)]
    public double LabelLocatorConfidence { get; set; } = 0.5;

    /// <summary>
    /// Margin area OCR di sekeliling kotak label (× sisi pendek kotak), di luar margin yang sudah ada di data
    /// training. Area dipotong ke batas kardus. Tuning case 01: kotak label kadang memotong digit terakhir, 0,3 cukup
    /// untuk menutupnya tanpa ikut membaca teks lain.
    /// </summary>
    [Range(0.0, 1.0)]
    public double LabelLocatorMargin { get; set; } = 0.3;

    /// <summary>
    /// Kotak label (+ margin) yang lebih sempit dari ini (px) diperbesar sampai selebar ini sebelum OCR, maksimal
    /// <see cref="OcrMaxUpscale"/> kali (sama seperti <see cref="OcrScanOptions.MinAreaWidth"/>). 0 = resolusi asli.
    /// </summary>
    [Range(0, 2000)]
    public int LabelLocatorMinAreaWidth { get; set; } = 320;

    /// <summary>
    /// Kotak label tegak (tinggi &gt; lebar, label diputar 90°) dibaca setelah diputar ke dua arah. Teks dari putaran
    /// hanya dipakai jika berupa tepat 8 angka asli (tanpa huruf mirip angka) dan keyakinannya ≥ nilai ini, karena
    /// putaran yang salah menghasilkan angka acak (aturan yang sama dengan pre-label data Carton Trainer).
    /// 0 = label tegak tidak dibaca.
    /// </summary>
    [Range(0.0, 1.0)]
    public double LabelLocatorRotatedMinConfidence { get; set; } = 0.9;

    /// <summary>
    /// Nama subfolder recognizer teks di <see cref="ModelsDirectory"/> (mis. <c>ppocr-rec-v4</c>: PP-OCRv4 rec ONNX,
    /// PaddleOCR/Baidu, Apache 2.0). OCR tahap 0a membaca kotak label locator langsung dengan model ini tanpa deteksi
    /// teks (puluhan kali lebih cepat dari Paddle penuh); kardus yang tidak terbaca yakin tetap dibaca Paddle.
    /// Kosong = nonaktif. Butuh <see cref="LabelLocatorModel"/>.
    /// </summary>
    public string LabelRecognizerModel { get; set; } = string.Empty;

    /// <summary>
    /// Hasil recognizer hanya dipakai jika keyakinannya ≥ nilai ini; di bawahnya kardus dibaca Paddle seperti biasa.
    /// Tuning case 01: ≥ 0,95 → 36 benar, 0 salah; ≥ 0,9 → 43 benar, 5 salah.
    /// </summary>
    [Range(0.0, 1.0)]
    public double LabelRecognizerMinConfidence { get; set; } = 0.95;

    /// <summary>
    /// <c>true</c> = kardus yang belum terbaca yakin oleh recognizer dibaca ulang PaddleOCR (kotak label + margin, label
    /// tegak, area label tetap, <see cref="OcrRetryScans"/>, <see cref="OcrFullCartonFallback"/>): lambat, ±90% waktu
    /// proses. <c>false</c> = OCR hanya label locator + recognizer (<see cref="LabelRecognizerModel"/>); semua hasil
    /// recognizer dipakai (<see cref="LabelRecognizerMinConfidence"/> diabaikan) dan model Paddle tidak dimuat.
    /// </summary>
    public bool PaddleOcrFallback { get; set; } = true;

    /// <summary>
    /// Kemiripan tampilan (cosine) minimal ke kardus acuan. Tuning case 01: kardus SKU &amp; desain sama 0,84–0,95,
    /// SKU lain ≤ 0,75.
    /// </summary>
    [Range(0.5, 1.0)]
    public double AppearanceMinSimilarity { get; set; } = 0.80;

    /// <summary>Jumlah kardus acuan ber-SKU sama yang harus lolos <see cref="AppearanceMinSimilarity"/>.</summary>
    [Range(1, 10)]
    public int AppearanceMinReferences { get; set; } = 1;

    /// <summary>Selisih kemiripan minimal antara SKU terbaik dan SKU lain.</summary>
    [Range(0.0, 0.5)]
    public double AppearanceMinMargin { get; set; } = 0.05;

    [Range(0.0, 1.0)]
    public double LowConfidenceThreshold { get; set; } = 0.6;

    /// <summary>
    /// Kotak yang luasnya ≥ nilai ini berada di dalam kotak lain dianggap duplikat dan dibuang
    /// (confidence lebih rendah). 0 = nonaktif.
    /// </summary>
    [Range(0.0, 1.0)]
    public double ContainmentThreshold { get; set; } = 0.8;
}

/// <summary>Satu scan baca ulang OCR (lihat <see cref="VisionOptions.OcrRetryScans"/>).</summary>
public sealed class OcrScanOptions
{
    /// <summary>Lebar area label (× lebar kardus) dari kiri-atas kardus.</summary>
    [Range(0.1, 1.0)]
    public double WidthRatio { get; set; } = 0.6;

    /// <summary>Tinggi area label (× tinggi kardus) dari atas kardus.</summary>
    [Range(0.1, 1.0)]
    public double HeightRatio { get; set; } = 0.3;

    /// <summary>
    /// Area (+ margin) yang lebih sempit dari ini (px) diperbesar sampai selebar ini, maksimal
    /// <see cref="VisionOptions.OcrMaxUpscale"/> kali. Angka label dus kecil di foto ±2000 px hanya ±8–13 px.
    /// </summary>
    [Range(0, 960)]
    public int MinAreaWidth { get; set; } = 320;
}

/// <summary>Pemeriksaan kelayakan foto (section <c>Photo</c>).</summary>
public sealed class PhotoOptions
{
    public const string SectionName = "Photo";

    /// <summary>Sisi terpanjang minimal (px).</summary>
    [Range(1, 20000)]
    public int MinLongSide { get; set; } = 2048;

    /// <summary>Jumlah piksel minimal (megapiksel). 0 = tidak dicek.</summary>
    [Range(0.0, 100.0)]
    public double MinMegapixels { get; set; } = 2.0;

    /// <summary>
    /// Gate stempel kamera berbasis OCR. Default mati: OCR penuh di strip atas/bawah memakan ±100 detik
    /// per foto di CPU 4 vCPU. Akan digantikan classifier kelayakan foto.
    /// </summary>
    public bool CameraOverlayEnabled { get; set; }

    [Range(0.05, 0.5)]
    public double OverlayScanRatio { get; set; } = 0.25;

    /// <summary>Dipakai jika <see cref="OverlayKeywords"/> kosong.</summary>
    public static IReadOnlyList<string> DefaultOverlayKeywords { get; } = ["GPS Map Camera", "Timemark", "Lat", "Long", "Kode Foto"];

    /// <summary>
    /// Kata kunci stempel kamera. Default kosong supaya binding konfigurasi mengganti (bukan menambah)
    /// daftar default.
    /// </summary>
    public IList<string> OverlayKeywords { get; } = [];

    public IReadOnlyList<string> EffectiveOverlayKeywords =>
        OverlayKeywords.Count > 0 ? [.. OverlayKeywords] : DefaultOverlayKeywords;

    [Range(1, 10)]
    public int OverlayMinKeywordHits { get; set; } = 2;

    /// <summary>
    /// Fast reject: nama subfolder model classifier kelayakan foto di <c>Vision:ModelsDirectory</c>
    /// (mis. <c>fast-reject-v3</c>). Kosong = gate classifier nonaktif.
    /// </summary>
    public string ClassifierModel { get; set; } = string.Empty;

    /// <summary>Fast reject: foto ditolak jika p(tolak) classifier ≥ nilai ini. Di bawahnya (ragu) foto lolos.</summary>
    [Range(0.5, 1.0)]
    public double ClassifierRejectThreshold { get; set; } = 0.7565;

    /// <summary>Fast reject: jumlah kardus terdeteksi minimal (setelah buang kotak bersarang). 0 = nonaktif.</summary>
    [Range(0, 1000)]
    public int MinCartonCount { get; set; } = 5;

    /// <summary>Fast reject: total luas kotak kardus / luas foto minimal. 0 = nonaktif.</summary>
    [Range(0.0, 1.0)]
    public double MinCartonCoverage { get; set; } = 0.08;
}

/// <summary>Filter lapisan depan (section <c>FrontLayer</c>).</summary>
public sealed class FrontLayerOptions
{
    public const string SectionName = "FrontLayer";

    [Range(0.1, 1.0)]
    public double MinWidthRatio { get; set; } = 0.6;

    [Range(0.05, 2.0)]
    public double MaxGapRatio { get; set; } = 0.35;

    /// <summary>Kotak teratas tumpukan yang tingginya &lt; rasio ini × kardus di bawahnya dianggap lapisan belakang (0 = nonaktif).</summary>
    [Range(0.0, 1.0)]
    public double MinTopHeightRatio { get; set; } = 0.55;
}

/// <summary>Batas upload (section <c>Upload</c>).</summary>
public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    [Range(1, 100)]
    public int MaxFileSizeMb { get; set; } = 10;

    public long MaxFileSizeBytes => MaxFileSizeMb * 1024L * 1024L;
}
