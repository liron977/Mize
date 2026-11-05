using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

/// <summary>
/// Data that arrives in the wrong format, over real HTTP with the real limiter (Redis) and real SQLite.
/// Every case must get a clear problem+json answer, must store nothing, and must not leak internals.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class MalformedInputApiTests(RedisFixture redis) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-malformed-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = $"{Guid.NewGuid():N}:";
    private readonly List<ApiInstance> _instances = new();

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private (HttpClient client, CapturingLoggerProvider logs) Start(string name, int limit = 1000)
    {
        var logs = new CapturingLoggerProvider();
        var api = new ApiInstance(redis.ConnectionString, Path.Combine(_dir, name + ".db"), _prefix, limit: limit, window: 60,
            configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        _instances.Add(api);
        return (api.CreateClient(), logs);
    }

    private static string Good(string supplier, string reservation = "r1", string checkIn = "\"2026-08-01T00:00:00Z\"", string price = "450.00") =>
        $"{{\"supplierId\":\"{supplier}\",\"reservationId\":\"{reservation}\",\"roomId\":\"room1\",\"checkIn\":{checkIn}," +
        $"\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":{price},\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}}";

    private static async Task<HttpResponseMessage> Post(HttpClient c, string body, string? contentType = "application/json")
    {
        var content = new StringContent(body, new UTF8Encoding(false));
        content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        return await c.PostAsync("/api/reservations/ingest", content);
    }


    private static async Task<JsonElement> Problem(HttpResponseMessage r)
    {
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json", "every error answer is a ProblemDetails, never an empty body");
        var text = await r.Content.ReadAsStringAsync();
        text.Should().NotContain("System.").And.NotContain("Exception").And.NotContain("   at ", "no internals or stack traces in an error body");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string[] Errors(JsonElement problem, string field) =>
        problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()!).ToArray();

    // The full matrix of bad shapes (types, timestamps, prices, identifier limits, years) lives in the unit tests
    // (IngestRequestParserTests, ValidatorTests). Here: one case per KIND of answer, to prove the HTTP wiring, the stored
    // state and the quota.

    // ---- the body as a whole

    [Fact]
    public async Task An_empty_body_is_a_400_that_says_so()
    {
        var (c, _) = Start("empty");
        var r = await Post(c, "");

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Errors(await Problem(r), "body").Single().Should().Contain("empty");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("{ not json")]
    [InlineData("{\"supplierId\":\"acme\",")]       // truncated
    [InlineData("{'supplierId':'acme'}")]            // single quotes
    [InlineData("{\"a\":1,}")]                       // trailing comma
    public async Task Text_that_is_not_valid_json_is_a_400_with_the_position_of_the_problem(string body)
    {
        var (c, _) = Start("notjson");
        var r = await Post(c, body);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Errors(await Problem(r), "body").Single().Should().Contain("not valid JSON").And.Contain("line 1");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Valid_json_that_is_not_an_object_is_a_400(string body)
    {
        var (c, _) = Start("notobject");
        var r = await Post(c, body);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Errors(await Problem(r), "body").Single().Should().Contain("must be a JSON object");
    }

    [Fact]
    public async Task Json_nested_far_too_deeply_is_rejected_not_a_crash()
    {
        var (c, _) = Start("deep");
        var r = await Post(c, string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200));

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Problem(r);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/xml")]
    [InlineData(null)]
    public async Task A_content_type_other_than_json_is_a_415_that_says_what_is_expected(string? contentType)
    {
        var (c, _) = Start("ctype");
        var r = await Post(c, Good("acme"), contentType);

        r.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await Problem(r)).GetProperty("detail").GetString().Should().Contain("application/json");
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("application/problem+json")]
    [InlineData("application/vnd.supplier+json")]
    public async Task Json_content_types_with_parameters_or_a_plus_json_suffix_are_accepted(string contentType)
    {
        var (c, _) = Start("ctype-ok");
        (await Post(c, Good("acme-" + Guid.NewGuid().ToString("N")[..6]), contentType)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_body_over_the_size_limit_is_a_413_and_is_never_processed()
    {
        var (c, _) = Start("big");
        var r = await Post(c, Good("acme").Replace("room1", new string('x', 20_000)));

        r.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await Problem(r)).GetProperty("detail").GetString().Should().Contain("16384");
        (await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/acme"))!.Ingested.Should().Be(0);
    }

    // A body with NO Content-Length (Transfer-Encoding: chunked) cannot be sent through the in-process test server: it buffers every
    // request and declares its length. That branch is covered by RequestBodyReaderTests (unit) and was checked by hand against the real
    // Kestrel server with curl (see docs/DESIGN.md).

    [Fact]
    public async Task A_missing_supplier_id_is_a_400_saying_it_is_required_and_costs_no_quota()
    {
        var (c, _) = Start("nosupplier", limit: 1);
        var body = "{\"reservationId\":\"r1\",\"roomId\":\"room1\",\"checkIn\":\"2026-08-01T00:00:00Z\",\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":450,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}";

        for (var i = 0; i < 3; i++)                                  // more than the limit of 1: there is no key to count them against
        {
            var r = await Post(c, body);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            Errors(await Problem(r), "supplierId").Single().Should().Contain("required");
        }
        var blank = await Post(c, body.Replace("{", "{\"supplierId\":\"   \","));
        Errors(await Problem(blank), "supplierId").Single().Should().Contain("required");
    }

    [Theory]
    [InlineData("roomId", "4.5", "number")]
    public async Task A_string_field_sent_as_any_other_json_type_is_rejected_naming_the_type_it_was(string field, string raw, string typeName)
    {
        var (c, _) = Start("wrongtype");
        var fields = new Dictionary<string, string>
        {
            ["supplierId"] = "\"acme\"", ["reservationId"] = "\"r1\"", ["roomId"] = "\"room1\"",
            ["checkIn"] = "\"2026-08-01T00:00:00Z\"", ["checkOut"] = "\"2026-08-03T00:00:00Z\"", ["price"] = "450", ["updatedAtUtc"] = "\"2026-07-01T10:15:00Z\""
        };
        fields[field] = raw;
        var body = "{" + string.Join(",", fields.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}";

        var r = await Post(c, body);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Errors(await Problem(r), field).Single().Should().Contain("must be a JSON string").And.Contain(typeName);
    }
    // ---- individual fields

    [Fact]
    public async Task Wrong_types_and_bad_timestamps_are_all_listed_in_one_400_and_nothing_is_stored()
    {
        var (c, _) = Start("fields");
        var supplier = "acme-" + Guid.NewGuid().ToString("N")[..6];
        var body = $"{{\"supplierId\":\"{supplier}\",\"reservationId\":true,\"roomId\":\"r\",\"checkIn\":\"2026-08-01T00:00:00\"," +
                   "\"checkOut\":1785000000,\"price\":\"450\",\"updatedAtUtc\":\"2026-07-01\"}";

        var r = await Post(c, body);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await Problem(r);
        Errors(problem, "reservationId").Single().Should().Contain("must be a JSON string");
        Errors(problem, "checkIn").Single().Should().Contain("explicit UTC offset");
        Errors(problem, "checkOut").Single().Should().Contain("ISO 8601");
        Errors(problem, "price").Single().Should().Contain("must be a JSON number");
        Errors(problem, "updatedAtUtc").Single().Should().Contain("explicit UTC offset");
        (await c.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{supplier}"))!.Ingested.Should().Be(0);
    }

    [Theory]
    [InlineData("\"2026-08-01T00:00:00\"")]    // no offset: previously accepted and silently read in the server's local time
    public async Task A_timestamp_without_an_explicit_offset_or_in_another_format_is_rejected_and_not_stored(string checkIn)
    {
        var (c, _) = Start("ts");
        var supplier = "acme-" + Guid.NewGuid().ToString("N")[..6];

        var r = await Post(c, Good(supplier, checkIn: checkIn));

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Errors(await Problem(r), "checkIn").Single().Should().Contain("2026-08-01T00:00:00Z");
        (await c.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{supplier}"))!.Ingested.Should().Be(0);
    }

    [Fact]
    public async Task The_same_instant_written_in_a_different_offset_is_the_same_reservation()
    {
        var (c, _) = Start("offset");
        var s = "acme-" + Guid.NewGuid().ToString("N")[..6];

        (await Post(c, Good(s, checkIn: "\"2026-08-01T00:00:00Z\""))).StatusCode.Should().Be(HttpStatusCode.Created);
        var again = await Post(c, Good(s, checkIn: "\"2026-08-01T02:00:00+02:00\""));   // 02:00 at +02:00 is 00:00Z

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadAsStringAsync()).Should().Contain("Unchanged");
    }

    [Theory]
    [InlineData("\"450\"")]
    [InlineData("450.005")]       // a number, but more than 2 decimals: rejected by the business rule
    public async Task A_price_that_is_not_a_valid_number_is_a_400_naming_the_price(string price)
    {
        var (c, _) = Start("price");
        var r = await Post(c, Good("acme-" + Guid.NewGuid().ToString("N")[..6], price: price));

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Errors(await Problem(r), "price").Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_oversized_supplier_id_is_a_400_and_so_is_asking_for_its_stats()
    {
        var (c, _) = Start("longsupplier");
        var tooLong = new string('s', 5000);

        var ingest = await Post(c, Good(tooLong));
        ingest.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Errors(await Problem(ingest), "supplierId").Single().Should().Contain("at most 100 characters");

        var stats = await c.GetAsync($"/api/reservations/stats/{new string('s', 150)}");
        stats.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Problem(stats);
    }

    // ---- leniency where it is safe

    [Fact]
    public async Task A_utf8_byte_order_mark_property_name_case_and_unknown_fields_are_tolerated()
    {
        var (c, _) = Start("lenient");
        var s = "acme-" + Guid.NewGuid().ToString("N")[..6];
        var json = $"{{\"SUPPLIERID\":\"{s}\",\"ReservationId\":\"r1\",\"roomId\":\"room1\",\"checkIn\":\"2026-08-01T00:00:00Z\"," +
                   "\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":450,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\",\"newSupplierField\":{\"x\":[1,2]}}";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        var r = await c.PostAsync("/api/reservations/ingest", content);

        r.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Surrounding_whitespace_in_identifiers_is_trimmed()
    {
        var (c, _) = Start("trim");
        var s = "acme-" + Guid.NewGuid().ToString("N")[..6];

        (await Post(c, Good($"  {s}  ", reservation: "  r1  "))).StatusCode.Should().Be(HttpStatusCode.Created);
        var again = await Post(c, Good(s, reservation: "r1"));

        (await again.Content.ReadAsStringAsync()).Should().Contain("Unchanged"); // same reservation: the padding did not create a second one
    }

    // ---- malformed traffic is still traffic

    [Fact]
    public async Task Malformed_requests_from_a_supplier_cost_quota_so_they_cannot_be_used_to_hammer_the_service()
    {
        var (c, _) = Start("quota", limit: 3);
        var s = "acme-" + Guid.NewGuid().ToString("N")[..6];

        for (var i = 0; i < 3; i++)
            (await Post(c, Good(s, price: "\"not a number\""))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await Post(c, Good(s, reservation: "r-valid"))).StatusCode.Should().Be(HttpStatusCode.TooManyRequests); // the valid one is throttled
    }

    [Fact]
    public async Task Unreadable_bodies_are_counted_as_malformed_and_logged_once_not_once_per_request()
    {
        var (c, logs) = Start("observe");

        for (var i = 0; i < 30; i++) (await Post(c, "{ not json")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var warnings = logs.Lines.Where(l => l.Level == LogLevel.Warning && l.Message.StartsWith("Malformed ingest request rejected")).ToList();
        warnings.Should().ContainSingle("30 identical rejections must produce one line, not 30");
        warnings[0].Message.Should().Contain("not valid JSON");

        var scrape = await (await c.GetAsync("/metrics")).Content.ReadAsStringAsync();
        scrape.Should().MatchRegex("reservations_ingest_requests_total\\{[^}]*outcome=\"malformed\"[^}]*\\}\\s+\\d+");
    }

    [Fact]
    public async Task A_valid_payload_still_works_exactly_as_before()
    {
        var (c, _) = Start("regression");
        var s = "acme-" + Guid.NewGuid().ToString("N")[..6];

        (await Post(c, Good(s))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Post(c, Good(s))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{s}")).Should().Be(new StatsDto(s, 1, 0, 1, 0));
    }
}
