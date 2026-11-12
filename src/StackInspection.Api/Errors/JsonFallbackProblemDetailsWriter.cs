using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace StackInspection.Api.Errors;

/// <summary>
/// Writer cadangan: selalu menulis <see cref="ProblemDetails"/> sebagai <c>application/problem+json</c>.
/// </summary>
/// <remarks>
/// Writer bawaan ASP.NET Core hanya menulis jika header <c>Accept</c> menerima JSON. Endpoint Preview
/// (<c>text/html</c>) dipanggil Swagger/browser dengan <c>Accept: text/html</c>, sehingga writer bawaan menolak
/// dan error 400/422/429 berubah menjadi 500 generik tanpa pesan. Writer ini didaftarkan paling akhir agar hanya
/// dipakai ketika writer bawaan menolak, sesuai kontrak "error tetap JSON".
/// </remarks>
internal sealed class JsonFallbackProblemDetailsWriter(
    IOptions<JsonOptions> jsonOptions,
    IOptions<ProblemDetailsOptions> problemOptions) : IProblemDetailsWriter
{
    private const string ProblemJsonContentType = "application/problem+json";

    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HttpContext http = context.HttpContext;
        ProblemDetails problem = context.ProblemDetails;
        problem.Status ??= http.Response.StatusCode;
        if (!problem.Extensions.ContainsKey("traceId"))
        {
            problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
        }

        problemOptions.Value.CustomizeProblemDetails?.Invoke(context);
        return new ValueTask(http.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            jsonOptions.Value.JsonSerializerOptions,
            ProblemJsonContentType,
            http.RequestAborted));
    }
}
