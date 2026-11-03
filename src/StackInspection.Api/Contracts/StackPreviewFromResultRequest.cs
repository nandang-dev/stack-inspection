using StackInspection.Application.Contracts;

namespace StackInspection.Api.Contracts;

/// <summary>Request endpoint Preview dari hasil (application/json): render hasil Collect SKU yang sudah ada tanpa foto.</summary>
public sealed class StackPreviewFromResultRequest
{
    /// <summary>
    /// Body response Collect SKU apa adanya, dari API ini atau dari sumber lain (mis. LLM) dengan skema yang sama.
    /// </summary>
    public CollectSkuResponse? CollectResult { get; init; }

    /// <summary>
    /// Data master SKU (opsional). Jika diisi, halaman menampilkan skor dan pelanggaran (Analyze); jika kosong, hanya
    /// susunan dus.
    /// </summary>
    public IReadOnlyList<MasterSkuDto>? MasterSkus { get; init; }
}
