using System.ComponentModel.DataAnnotations;

namespace StackInspection.Api.Contracts;

/// <summary>
/// Parameter query endpoint Preview dari hasil. Body-nya response Collect SKU apa adanya, sehingga hasil Collect SKU
/// (atau output LLM berskema sama) bisa langsung ditempel tanpa dibungkus.
/// </summary>
public sealed class StackPreviewFromResultRequest
{
    /// <summary>
    /// Data master SKU dalam bentuk JSON array (opsional), sama seperti field <c>masterSkus</c> di Preview. Jika diisi,
    /// halaman menampilkan skor dan pelanggaran (Analyze); jika kosong, hanya susunan dus.
    /// </summary>
    public string? MasterSkus { get; init; }

    /// <summary>
    /// Token input LLM (opsional, ≥ 0): Anthropic <c>usage.input_tokens</c> (+ token cache jika ada), OpenAI
    /// <c>usage.prompt_tokens</c>, Gemini <c>usageMetadata.promptTokenCount</c>.
    /// </summary>
    [Range(0, long.MaxValue)]
    public long? InputTokens { get; init; }

    /// <summary>
    /// Token output LLM (opsional, ≥ 0): Anthropic <c>usage.output_tokens</c>, OpenAI <c>usage.completion_tokens</c>,
    /// Gemini <c>usageMetadata.candidatesTokenCount</c>.
    /// </summary>
    [Range(0, long.MaxValue)]
    public long? OutputTokens { get; init; }

    /// <summary>Token yang ditampilkan di halaman; null jika keduanya tidak diisi.</summary>
    public TokenUsageDto? ToTokenUsage() => InputTokens is null && OutputTokens is null
        ? null
        : new TokenUsageDto { InputTokens = InputTokens ?? 0, OutputTokens = OutputTokens ?? 0 };
}
