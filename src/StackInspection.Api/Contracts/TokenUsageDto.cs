using System.ComponentModel.DataAnnotations;

namespace StackInspection.Api.Contracts;

/// <summary>
/// Pemakaian token LLM yang menghasilkan <c>collectResult</c>, disalin dari field usage respons LLM:
/// Anthropic <c>usage.input_tokens</c>/<c>usage.output_tokens</c>, OpenAI <c>usage.prompt_tokens</c>/
/// <c>usage.completion_tokens</c>, Gemini <c>usageMetadata.promptTokenCount</c>/<c>usageMetadata.candidatesTokenCount</c>.
/// </summary>
public sealed class TokenUsageDto
{
    /// <summary>Token input (prompt + gambar). Pada Anthropic, tambahkan token cache read/creation jika ada.</summary>
    [Range(0, long.MaxValue)]
    public long InputTokens { get; init; }

    /// <summary>Token output (JSON yang dihasilkan LLM, termasuk token reasoning/thinking jika ada).</summary>
    [Range(0, long.MaxValue)]
    public long OutputTokens { get; init; }

    /// <summary>Total token = <c>inputTokens</c> + <c>outputTokens</c> (dihitung server).</summary>
    public long TotalTokens => InputTokens + OutputTokens;
}
