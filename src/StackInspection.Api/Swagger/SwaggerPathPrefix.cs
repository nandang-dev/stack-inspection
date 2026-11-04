using System.Text.RegularExpressions;

namespace StackInspection.Api.Swagger;

/// <summary>Membaca prefix path dari reverse proxy (header X-Forwarded-Prefix) untuk server Swagger.</summary>
public static partial class SwaggerPathPrefix
{
    /// <summary>
    /// Mengembalikan prefix yang aman dipakai sebagai URL server (mis. "/stack-inspection"), atau null bila
    /// header kosong/tidak valid. Hanya segmen path sederhana yang diterima agar header palsu tidak bisa
    /// mengarahkan Swagger UI ke host lain.
    /// </summary>
    public static string? FromHeader(string? header)
    {
        // Proxy bisa mengirim beberapa nilai dipisah koma; yang pertama paling luar.
        string? prefix = header?.Split(',')[0].Trim().TrimEnd('/');
        return string.IsNullOrEmpty(prefix) || !PrefixPattern().IsMatch(prefix) ? null : prefix;
    }

    [GeneratedRegex("^(/[A-Za-z0-9._~-]+)+$")]
    private static partial Regex PrefixPattern();
}
