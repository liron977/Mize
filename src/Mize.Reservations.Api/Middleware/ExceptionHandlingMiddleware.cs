using Microsoft.AspNetCore.Mvc;
using Mize.Reservations.Api.Observability;
using Mize.Reservations.Core.Abstractions;

namespace Mize.Reservations.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, LogSampler sampler)
{
    // During an outage every request fails the same way; log the first, then at most one line per interval with the skipped count.
    // (Every failure is still counted by the reservations.ingest.requests{outcome="error"} metric.)
    private static readonly TimeSpan OutageLogInterval = TimeSpan.FromSeconds(10);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // client went away; nothing to report
        }
        catch (BadHttpRequestException ex)
        {
            // The framework could not read the request (for example a body over a server limit). Use its status (413, 408, 400, ...)
            // but never echo its message: it can carry internals.
            var status = ex.StatusCode is >= 400 and < 500 ? ex.StatusCode : StatusCodes.Status400BadRequest;
            await Write(context, status,
                status == StatusCodes.Status413PayloadTooLarge ? "Request body too large." : "Malformed request.",
                status == StatusCodes.Status413PayloadTooLarge ? "The request body exceeds the size limit." : "The request could not be read.");
        }
        catch (RateLimiterUnavailableException ex)
        {
            // Fail closed: the limiter protects the table, and suppliers retry on 5xx.
            if (sampler.ShouldLog("ratelimiter-unavailable", OutageLogInterval, out var skipped))
                logger.LogError(ex, "Rate limiter unavailable; rejecting requests with 503 ({Skipped} similar failures since the last line were not logged).", skipped);
            await Write(context, StatusCodes.Status503ServiceUnavailable, "Service temporarily unavailable.", "Please retry shortly.", retryAfterSeconds: 5);
        }
        catch (StorageBusyException ex)
        {
            if (sampler.ShouldLog("storage-busy", OutageLogInterval, out var skipped))
                logger.LogWarning(ex, "Storage busy or write queue full; rejecting requests with 503 ({Skipped} similar failures since the last line were not logged).", skipped);
            await Write(context, StatusCodes.Status503ServiceUnavailable, "Service temporarily unavailable.", "Please retry shortly.", retryAfterSeconds: 2);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception."); // never sampled: it is unexpected and every one matters
            await Write(context, StatusCodes.Status500InternalServerError, "An unexpected error occurred.", null);
        }
    }

    private static Task Write(HttpContext context, int status, string title, string? detail, int? retryAfterSeconds = null)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.Clear(); // clears headers too, so set Retry-After afterwards
        context.Response.StatusCode = status;
        if (retryAfterSeconds is { } s) context.Response.Headers.RetryAfter = s.ToString();
        return context.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = status, Title = title, Detail = detail },
            options: null, contentType: "application/problem+json");
    }
}
