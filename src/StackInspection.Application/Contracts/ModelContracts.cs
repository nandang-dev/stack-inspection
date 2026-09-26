namespace StackInspection.Application.Contracts;

/// <summary>Metrik model di golden test set Carton Trainer.</summary>
public sealed class ModelMetricsDto
{
    /// <summary>Precision (0–1).</summary>
    public double Precision { get; init; }

    /// <summary>Recall (0–1).</summary>
    public double Recall { get; init; }

    /// <summary>mAP pada IoU 0.5.</summary>
    public double Map50 { get; init; }

    /// <summary>mAP rata-rata IoU 0.5–0.95.</summary>
    public double Map50To95 { get; init; }
}

/// <summary>Satu model deteksi yang tersedia di server.</summary>
public sealed class ModelInfoDto
{
    /// <summary>Nama model (dipakai di field <c>model</c> Collect SKU), misalnya <c>carton-v1</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>true jika dipakai saat request tidak memilih model.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Ukuran input model (piksel).</summary>
    public int InputSize { get; init; }

    /// <summary>Confidence threshold yang dipakai.</summary>
    public double ConfidenceThreshold { get; init; }

    /// <summary>IoU threshold NMS yang dipakai.</summary>
    public double IouThreshold { get; init; }

    /// <summary>Status dari Carton Trainer: <c>candidate</c>, <c>approved</c>, <c>rejected</c>; null jika tanpa model card.</summary>
    public string? Status { get; init; }

    /// <summary>Model dasar (silsilah), misalnya <c>yolox-tiny</c> atau <c>carton-v1</c>.</summary>
    public string? BaseModel { get; init; }

    /// <summary>Versi dataset yang dipakai training.</summary>
    public string? DatasetVersion { get; init; }

    /// <summary>Waktu selesai training.</summary>
    public DateTimeOffset? TrainedAt { get; init; }

    /// <summary>Metrik di golden test set; null jika belum dievaluasi.</summary>
    public ModelMetricsDto? Metrics { get; init; }
}
