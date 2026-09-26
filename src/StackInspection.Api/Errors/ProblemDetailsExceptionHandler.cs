using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using StackInspection.Application.Exceptions;

namespace StackInspection.Api.Errors;

/// <summary>Memetakan exception aplikasi ke <see cref="ProblemDetails"/> (400 / 422 / 500).</summary>
internal sealed partial class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public const string PhotoProblemType = "https://planogram.local/problems/photo-not-inspectable";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case RequestValidationException validation:
                problem = new ValidationProblemDetails(validation.Errors.ToDictionary(e => e.Key, e => e.Value))
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Request tidak valid",
                };
                break;
            case InvalidImageException invalidImage:
                problem = new ValidationProblemDetails(new Dictionary<string, string[]> { ["image"] = [invalidImage.Message] })
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Gambar tidak bisa dibaca",
                };
                break;
            case PhotoNotInspectableException photo:
                problem = new ProblemDetails
                {
                    Type = PhotoProblemType,
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = "Foto tidak layak dinilai",
                    Detail = photo.Message,
                };
                problem.Extensions["code"] = PhotoNotInspectableException.Code;
                problem.Extensions["reason"] = photo.Reason;
                break;
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                return true; // client memutus koneksi
            default:
                LogUnhandled(logger, exception);
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Terjadi kesalahan pada server",
                };
                break;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        }).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error tak tertangani")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
