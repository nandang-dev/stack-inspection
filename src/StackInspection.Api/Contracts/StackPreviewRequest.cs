namespace StackInspection.Api.Contracts;

/// <summary>Request endpoint Preview (multipart/form-data): Collect SKU + Analyze dalam satu panggilan.</summary>
public sealed class StackPreviewRequest
{
    /// <summary>Foto JPG/PNG, maksimal <c>Upload:MaxFileSizeMb</c>.</summary>
    public IFormFile? Image { get; init; }

    /// <summary>Daftar kode SKU untuk fuzzy match (opsional, boleh dipisah koma). Sama seperti Collect SKU.</summary>
    public List<string>? CandidateSkus { get; init; }

    /// <summary>Model deteksi (opsional). Kosong = model default.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Master SKU dalam bentuk JSON array <c>[{"sku","name","maxStack","weight","class"}]</c> (opsional).
    /// Jika diisi, halaman menampilkan skor dan pelanggaran (Analyze); jika kosong, hanya susunan dus.
    /// </summary>
    public string? MasterSkus { get; init; }
}
