using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace StackInspection.Api.Security;

/// <summary>
/// Menolak request ke <c>/api</c> tanpa header <c>X-Api-Key</c> yang terdaftar di <see cref="ApiKeyOptions.ApiKeys"/>
/// dengan 401 <c>API_KEY_INVALID</c>. <c>/health</c> dan Swagger UI tetap terbuka; "Try it out" memakai tombol Authorize.
/// </summary>
internal sealed partial class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";

    public const string InvalidCode = "API_KEY_INVALID";

    public const string InvalidProblemType = "https://planogram.local/problems/api-key-invalid";

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private readonly bool _enabled;

    // Disimpan sebagai hash SHA-256 agar perbandingan selalu sama panjang dan waktu konstan.
    private readonly byte[][] _keys;

    public ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _enabled = options.Value.Enabled;
        _keys = [.. options.Value.ParseKeys().Select(Hash)];

        if (!_enabled)
        {
            LogAuthDisabled(logger);
        }
        else if (_keys.Length == 0)
        {
            LogNoKeys(logger);
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled || !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        byte[]? key = Match(context.Request.Headers[HeaderName]);
        if (key is null)
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        // Log hanya memuat potongan hash key, cukup untuk membedakan pemakai tanpa membocorkan key-nya.
        using (_logger.BeginScope(new Dictionary<string, object> { ["ApiKeyId"] = Convert.ToHexString(key, 0, 4) }))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    private byte[]? Match(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        byte[] hash = Hash(key.Trim());
        byte[]? match = null;
        foreach (byte[] candidate in _keys)
        {
            // Tidak berhenti di kecocokan pertama agar waktu respons tidak membocorkan urutan key.
            if (CryptographicOperations.FixedTimeEquals(hash, candidate))
            {
                match ??= candidate;
            }
        }

        return match;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = HeaderName;
        ProblemDetails problem = new()
        {
            Type = InvalidProblemType,
            Status = StatusCodes.Status401Unauthorized,
            Title = "API key tidak valid",
            Detail = $"Kirim API key yang terdaftar di header {HeaderName}.",
        };
        problem.Extensions["code"] = InvalidCode;
        return context.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem })
            .AsTask();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth:Enabled = false: semua endpoint /api terbuka tanpa API key")]
    private static partial void LogAuthDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AUTH_APIKeys kosong: semua request ke /api akan ditolak 401")]
    private static partial void LogNoKeys(ILogger logger);
}
