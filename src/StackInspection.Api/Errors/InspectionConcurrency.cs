using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StackInspection.Application.Configuration;

namespace StackInspection.Api.Errors;

/// <summary>
/// Pembatas jumlah proses vision yang berjalan bersamaan (<see cref="VisionOptions.MaxConcurrentInspections"/>).
/// Request di luar batas langsung ditolak 429 <c>SERVER_BUSY</c>: request yang diantrekan tetap memakan RAM,
/// dan client/proxy biasanya sudah timeout sebelum gilirannya tiba.
/// </summary>
internal static class InspectionConcurrency
{
    public const string PolicyName = "inspection";

    public const string BusyCode = "SERVER_BUSY";

    public const string BusyProblemType = "https://planogram.local/problems/server-busy";

    private const int RetryAfterSeconds = 30;

    public static IServiceCollection AddInspectionConcurrency(this IServiceCollection services, IConfiguration configuration)
    {
        VisionOptions vision = configuration.GetSection(VisionOptions.SectionName).Get<VisionOptions>() ?? new VisionOptions();
        return services.AddRateLimiter(options =>
        {
            options.AddConcurrencyLimiter(PolicyName, limiter =>
            {
                limiter.PermitLimit = vision.MaxConcurrentInspections;
                limiter.QueueLimit = 0;
            });
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                HttpContext http = context.HttpContext;
                http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ProblemDetails problem = new()
                {
                    Type = BusyProblemType,
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Server sedang memproses foto lain",
                    Detail = $"Coba lagi dalam {RetryAfterSeconds} detik.",
                };
                problem.Extensions["code"] = BusyCode;
                await http.RequestServices.GetRequiredService<IProblemDetailsService>()
                    .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem })
                    .ConfigureAwait(false);
            };
        });
    }
}
