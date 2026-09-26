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

    [Range(0.1, 1.0)]
    public double LabelCropWidthRatio { get; set; } = 0.6;

    [Range(0.1, 1.0)]
    public double LabelCropHeightRatio { get; set; } = 0.3;

    [Range(0, 4)]
    public int FuzzyMaxDistance { get; set; } = 2;

    [Range(0.0, 1.0)]
    public double LowConfidenceThreshold { get; set; } = 0.6;
}

/// <summary>Pemeriksaan kelayakan foto (section <c>Photo</c>).</summary>
public sealed class PhotoOptions
{
    public const string SectionName = "Photo";

    [Range(1, 20000)]
    public int MinLongSide { get; set; } = 3000;

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
}

/// <summary>Filter lapisan depan (section <c>FrontLayer</c>).</summary>
public sealed class FrontLayerOptions
{
    public const string SectionName = "FrontLayer";

    [Range(0.1, 1.0)]
    public double MinWidthRatio { get; set; } = 0.6;

    [Range(0.05, 2.0)]
    public double MaxGapRatio { get; set; } = 0.35;
}

/// <summary>Batas upload (section <c>Upload</c>).</summary>
public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    [Range(1, 100)]
    public int MaxFileSizeMb { get; set; } = 10;

    public long MaxFileSizeBytes => MaxFileSizeMb * 1024L * 1024L;
}
