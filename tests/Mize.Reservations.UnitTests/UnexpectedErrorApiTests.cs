using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.IntegrationTests;
using Mize.Reservations.RateLimiting.InMemory;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>
/// A repository that throws, injected into the real host: proves the caller gets a generic 500, nothing leaks, every failure is logged
/// with its exception and counted. Fault injection with a fake dependency, so it lives with the unit tests.
/// </summary>
public sealed class UnexpectedErrorApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-boom-" + Guid.NewGuid().ToString("N"));
    private readonly List<ApiInstance> _instances = new();

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private (HttpClient client, CapturingLoggerProvider logs) Start(Action<IServiceCollection> extra)
    {
        var logs = new CapturingLoggerProvider();
        var api = new ApiInstance("127.0.0.1:1", Path.Combine(_dir, "boom.db"), "unused:", limit: 100_000, window: 60,
            configureServices: s =>
            {
                s.AddSingleton<ILoggerProvider>(logs);
                s.RemoveAll<IRateLimiter>();
                s.AddInMemoryRateLimiting();
                extra(s);
            });
        _instances.Add(api);
        return (api.CreateClient(), logs);
    }

    private static string Json(string supplier, string reservation) =>
        $"{{\"supplierId\":\"{supplier}\",\"reservationId\":\"{reservation}\",\"roomId\":\"room1\",\"checkIn\":\"2026-08-01T00:00:00Z\"," +
        "\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":450.00,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}";

    private static async Task<HttpResponseMessage> Post(HttpClient c, string json)
    {
        var content = new StringContent(json, new UTF8Encoding(false));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        return await c.PostAsync("/api/reservations/ingest", content);
    }

    private sealed class ExplodingRepository : IReservationRepository
    {
        public Task<IngestOutcome> UpsertAsync(Reservation reservation, CancellationToken ct = default) =>
            throw new InvalidOperationException("secret internal detail: Server=db01;Password=hunter2");

        public Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("secret internal detail: Server=db01;Password=hunter2");
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_generic_500_that_leaks_nothing_and_is_logged_with_its_exception_every_time()
    {
        var (c, logs) = Start(s =>
        {
            s.RemoveAll<IReservationRepository>();
            s.AddSingleton<IReservationRepository, ExplodingRepository>();
        });

        var first = await Post(c, Json("acme", "r1"));
        var second = await Post(c, Json("acme", "r2"));

        foreach (var r in new[] { first, second })
        {
            r.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            r.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var body = await r.Content.ReadAsStringAsync();
            body.Should().Contain("An unexpected error occurred.");
            body.Should().NotContain("secret").And.NotContain("hunter2").And.NotContain("InvalidOperation").And.NotContain("Server=");
        }

        // unexpected errors are never sampled: each one is logged, with the exception (the secret stays in the log, not in the response)
        var errors = logs.Lines.Where(l => l.Level == LogLevel.Error && l.Message.Contains("Unhandled exception")).ToList();
        errors.Should().HaveCount(2);
        errors.Should().OnlyContain(l => l.Exception is InvalidOperationException);

        (await Scrape(c)).Should().MatchRegex("reservations_ingest_requests_total\\{[^}]*outcome=\"error\"[^}]*\\}\\s+\\d+");
    }


    private static async Task<string> Scrape(HttpClient c) => await (await c.GetAsync("/metrics")).Content.ReadAsStringAsync();
}
