using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

/// <summary>checkOut must be after checkIn, end to end over HTTP with the real limiter (Redis) and real SQLite.</summary>
[Collection(RedisCollection.Name)]
public sealed class StayDatesApiTests(RedisFixture redis) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-stay-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = $"{Guid.NewGuid():N}:";
    private ApiInstance? _api;
    private HttpClient? _clientBacking;
    private HttpClient _client => _clientBacking ??= (_api = new ApiInstance(redis.ConnectionString, Path.Combine(_dir, "api.db"), _prefix, limit: 1000, window: 60)).CreateClient();

    public void Dispose()
    {
        _api?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static string Supplier() => "stay-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpResponseMessage> Post(string supplier, string reservation, string checkIn, string checkOut,
        string price = "450.00", string updated = "2026-07-01T10:15:00Z")
    {
        var json = $"{{\"supplierId\":\"{supplier}\",\"reservationId\":\"{reservation}\",\"roomId\":\"room1\",\"checkIn\":\"{checkIn}\"," +
                   $"\"checkOut\":\"{checkOut}\",\"price\":{price},\"updatedAtUtc\":\"{updated}\"}}";
        var content = new StringContent(json, new UTF8Encoding(false));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        return await _client.PostAsync("/api/reservations/ingest", content);
    }

    private Task<StatsDto?> Stats(string supplier) => _client.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{supplier}");

    [Theory]
    [InlineData("2026-08-01T00:00:00Z", "2026-08-03T00:00:00Z", HttpStatusCode.Created)]            // a normal stay
    [InlineData("2026-08-01T00:00:00Z", "2026-08-01T00:00:01Z", HttpStatusCode.Created)]            // one second is enough
    [InlineData("2026-08-01T22:00:00Z", "2026-08-01T20:00:00-05:00", HttpStatusCode.Created)]       // later by instant (01:00Z next day) although the clock text 20:00 looks earlier than 22:00
    [InlineData("2026-08-01T00:00:00Z", "2026-08-01T00:00:00Z", HttpStatusCode.BadRequest)]         // equal
    [InlineData("2026-08-01T00:00:01Z", "2026-08-01T00:00:00Z", HttpStatusCode.BadRequest)]         // one second before
    [InlineData("2026-08-03T00:00:00Z", "2026-08-01T00:00:00Z", HttpStatusCode.BadRequest)]         // days before
    [InlineData("2026-08-01T02:00:00+02:00", "2026-08-01T00:00:00Z", HttpStatusCode.BadRequest)]    // the same instant in two offsets
    [InlineData("2026-08-01T05:00:00Z", "2026-08-01T09:00:00+10:00", HttpStatusCode.BadRequest)]    // later-looking clock, EARLIER instant
    public async Task Only_a_checkOut_strictly_later_by_instant_is_accepted(string checkIn, string checkOut, HttpStatusCode expected)
    {
        var supplier = Supplier();

        var r = await Post(supplier, "r1", checkIn, checkOut);

        r.StatusCode.Should().Be(expected);
        (await Stats(supplier))!.Ingested.Should().Be(expected == HttpStatusCode.Created ? 1 : 0); // a rejected stay is never stored
    }

    [Fact]
    public async Task The_400_names_the_field_and_shows_both_values_in_utc()
    {
        var r = await Post(Supplier(), "r1", "2026-08-03T00:00:00Z", "2026-08-01T02:00:00+02:00");

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var message = doc.RootElement.GetProperty("errors").GetProperty("checkOut").EnumerateArray().Single().GetString()!;
        message.Should().Contain("checkOut must be after checkIn")
            .And.Contain("checkIn 2026-08-03T00:00:00Z")
            .And.Contain("checkOut 2026-08-01T00:00:00Z");   // 02:00+02:00 shown as the instant it is
    }

    [Fact]
    public async Task An_invalid_update_is_rejected_and_the_stored_reservation_stays_exactly_as_it_was()
    {
        var s = Supplier();
        (await Post(s, "r1", "2026-08-01T00:00:00Z", "2026-08-03T00:00:00Z", price: "450.00", updated: "2026-07-01T10:15:00Z"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // a NEWER update that would change the price, but whose stay ends before it starts
        var bad = await Post(s, "r1", "2026-08-05T00:00:00Z", "2026-08-02T00:00:00Z", price: "999.00", updated: "2026-07-20T10:15:00Z");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // the original is still stored untouched: re-sending it is a pure duplicate, not an update
        var again = await Post(s, "r1", "2026-08-01T00:00:00Z", "2026-08-03T00:00:00Z", price: "450.00", updated: "2026-07-01T10:15:00Z");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadAsStringAsync()).Should().Contain("Unchanged");
        (await Stats(s)).Should().Be(new StatsDto(s, 1, 0, 1, 0));
    }

    [Fact]
    public async Task A_valid_update_that_moves_the_dates_is_still_applied()
    {
        var s = Supplier();
        await Post(s, "r1", "2026-08-01T00:00:00Z", "2026-08-03T00:00:00Z");

        var extended = await Post(s, "r1", "2026-08-01T00:00:00Z", "2026-08-07T00:00:00Z", updated: "2026-07-05T10:15:00Z");

        extended.StatusCode.Should().Be(HttpStatusCode.OK);
        (await extended.Content.ReadAsStringAsync()).Should().Contain("Updated");
    }

    [Fact]
    public async Task All_the_date_problems_come_back_together_with_other_problems()
    {
        var s = Supplier();
        var json = $"{{\"supplierId\":\"{s}\",\"reservationId\":\"r1\",\"roomId\":\"room1\",\"checkIn\":\"2026-08-03T00:00:00Z\"," +
                   "\"checkOut\":\"2026-08-01T00:00:00Z\",\"price\":-5,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}";
        var content = new StringContent(json, new UTF8Encoding(false));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        var r = await _client.PostAsync("/api/reservations/ingest", content);

        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("checkOut", "price");
    }
}
