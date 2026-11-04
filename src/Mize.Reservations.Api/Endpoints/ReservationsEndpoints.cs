using System.Diagnostics;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using Mize.Reservations.Api.Contracts;
using Mize.Reservations.Api.Observability;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Mize.Reservations.Core.Telemetry;

namespace Mize.Reservations.Api.Endpoints;

public static class ReservationsEndpoints
{
    public static void MapReservations(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reservations");

        // The handler reads the body itself (instead of letting the framework bind a DTO) so that EVERY way a payload can be
        // wrong gets a clear, consistent ProblemDetails answer: wrong content type, too large, empty, not JSON, not an object,
        // wrong field types, bad timestamps. The framework's own binding failures answer 400/415 with an empty body.
        group.MapPost("/ingest", async (HttpContext http, IngestService service, IngestLogger log, ReservationMetrics metrics) =>
        {
            var started = Stopwatch.GetTimestamp();

            IResult Malformed(int status, string reason)
            {
                metrics.RecordIngest("malformed", Stopwatch.GetElapsedTime(started));
                log.Malformed(reason);
                return status == StatusCodes.Status400BadRequest
                    ? Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = [reason] }, title: "Invalid reservation payload.")
                    : Results.Problem(title: status == StatusCodes.Status413PayloadTooLarge ? "Request body too large." : "Unsupported media type.",
                        detail: reason, statusCode: status);
            }

            if (!IsJson(http.Request.ContentType))
                return Malformed(StatusCodes.Status415UnsupportedMediaType, "The Content-Type must be application/json.");

            if (http.Request.ContentLength > IngestLimits.MaxBodyBytes)
                return Malformed(StatusCodes.Status413PayloadTooLarge, $"The request body must be at most {IngestLimits.MaxBodyBytes} bytes.");

            var body = await RequestBodyReader.ReadAtMostAsync(http.Request.Body, IngestLimits.MaxBodyBytes, http.RequestAborted);
            if (body is null)
                return Malformed(StatusCodes.Status413PayloadTooLarge, $"The request body must be at most {IngestLimits.MaxBodyBytes} bytes.");

            ReadOnlyMemory<byte> payload = body.AsMemory(HasBom(body) ? 3 : 0);   // a UTF-8 byte order mark is tolerated
            if (payload.Span.IsWhiteSpaceOnly())
                return Malformed(StatusCodes.Status400BadRequest, "The request body is empty; send a JSON object.");

            IngestCommand command;
            try
            {
                using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
                command = IngestRequestParser.Parse(doc.RootElement);
            }
            catch (JsonException ex)
            {
                var where = ex.LineNumber is { } line ? $" (line {line + 1}, position {(ex.BytePositionInLine ?? 0) + 1})" : "";
                return Malformed(StatusCodes.Status400BadRequest, "The request body is not valid JSON" + where + ".");
            }

            var result = await service.IngestAsync(command, http.RequestAborted);
            log.Completed(command.SupplierId, command.ReservationId, result, started); // Debug per request; sampled Warning when throttled

            switch (result.Outcome)
            {
                case IngestOutcome.Invalid:
                    return Results.ValidationProblem(
                        (IDictionary<string, string[]>)result.Errors!, title: "Invalid reservation payload.");

                case IngestOutcome.Throttled:
                    var seconds = (int)Math.Max(1, Math.Ceiling(result.RetryAfter!.Value.TotalSeconds));
                    http.Response.Headers.RetryAfter = seconds.ToString();
                    return Results.Problem(
                        title: "Too many requests.",
                        detail: $"Rate limit exceeded for this supplier. Retry in {seconds}s.",
                        statusCode: StatusCodes.Status429TooManyRequests,
                        // NOT "status": that is already the standard ProblemDetails member (the numeric 429); a second "status" key
                        // produced a body with a duplicate key whose last value ("Throttled") many parsers keep.
                        extensions: new Dictionary<string, object?> { ["outcome"] = "Throttled" });

                case IngestOutcome.Created:
                    return Results.Json(new IngestResponse("Created", true), statusCode: StatusCodes.Status201Created);

                default: // Updated, Unchanged, Stale, Expired
                    return Results.Ok(new IngestResponse(result.Outcome.ToString(), result.Changed));
            }
        });

        group.MapGet("/stats/{supplierId}", async (string supplierId, StatsService service, CancellationToken ct) =>
        {
            if (IngestLimits.IdentifierProblem(supplierId.Trim(), IngestLimits.MaxSupplierIdLength) is { } problem)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["supplierId"] = ["supplierId " + problem] },
                    title: "Invalid supplier id.");

            var s = await service.GetAsync(supplierId, ct);
            return Results.Ok(new StatsResponse(s.SupplierId, s.Ingested, s.Throttled, s.Unchanged, s.Stale));
        });
    }

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mt)
        && mt.MediaType.HasValue
        && (mt.MediaType.Value.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mt.MediaType.Value.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static bool HasBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
}

internal static class BodyExtensions
{
    public static bool IsWhiteSpaceOnly(this ReadOnlySpan<byte> s)
    {
        foreach (var b in s) if (b is not (0x20 or 0x09 or 0x0A or 0x0D)) return false;
        return true;
    }
}
