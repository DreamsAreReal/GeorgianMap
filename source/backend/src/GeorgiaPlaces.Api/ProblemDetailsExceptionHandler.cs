using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace GeorgiaPlaces.Api;

/// <summary>
/// Single sink for unhandled exceptions. Emits RFC 7807 ProblemDetails (TZ §8.0).
/// Sentry already captured the exception via Sentry.AspNetCore middleware before
/// we get here.
/// </summary>
public sealed partial class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment env,
    ILogger<ProblemDetailsExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // OperationCanceledException on a cancelled request is not a bug — log Debug, return.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogClientCancelled(logger);
            return true;
        }

        LogUnhandled(logger, exception);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var problem = new ProblemDetails
        {
            Type = "https://georgia-places.example/problems/internal-error",
            Title = "An unexpected error occurred.",
            Status = StatusCodes.Status500InternalServerError,
            Detail = env.IsDevelopment() ? exception.ToString() : "Internal server error.",
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Request was cancelled by client.")]
    static partial void LogClientCancelled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Unhandled exception in pipeline.")]
    static partial void LogUnhandled(ILogger logger, Exception exception);
}
