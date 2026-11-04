using FluentAssertions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.UnitTests;

public class ArchiveFormatAndLayoutTests
{
    private static Reservation R(string supplier, string id, DateTime checkOut, int price = 100) =>
        new(supplier, id, "room", checkOut.AddDays(-2), checkOut, price, new DateTime(2024, 7, 1, 10, 15, 0, DateTimeKind.Utc).AddTicks(1234567));

    private static DateTime Date(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Format_round_trips_with_full_precision_and_is_gzipped_json_lines()
    {
        var rows = new[] { R("acme", "r1", Date(2024, 8, 3)), R("acme", "r2", Date(2024, 8, 4), 7) };

        var bytes = ArchiveFormat.Serialize(rows);

        bytes[0].Should().Be(0x1f); bytes[1].Should().Be(0x8b); // gzip magic: readable by any tool
        ArchiveFormat.Deserialize(bytes).Should().Equal(rows);
    }

    [Fact]
    public void Records_are_versioned_and_self_describing()
    {
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(ArchiveFormat.Serialize([R("acme", "r1", Date(2024, 8, 3))])),
            System.IO.Compression.CompressionMode.Decompress);
        var text = new StreamReader(gz).ReadToEnd();

        text.Should().Contain("\"v\":1").And.Contain("\"supplierId\":\"acme\"").And.Contain("\"priceCents\":100");
        text.TrimEnd().Split('\n').Should().HaveCount(1); // one record per line
    }

    [Fact]
    public void An_unknown_record_version_is_refused_rather_than_misread()
    {
        using var output = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, true))
        using (var w = new StreamWriter(gz))
            w.WriteLine("{\"v\":99,\"supplierId\":\"a\",\"reservationId\":\"r\",\"roomId\":\"x\",\"checkIn\":\"2024-01-01T00:00:00.0000000Z\",\"checkOut\":\"2024-01-02T00:00:00.0000000Z\",\"priceCents\":1,\"updatedAtUtc\":\"2024-01-01T00:00:00.0000000Z\"}");

        var act = () => ArchiveFormat.Deserialize(output.ToArray());
        act.Should().Throw<InvalidDataException>().WithMessage("*version 99*");
    }

    [Fact]
    public void Rows_are_partitioned_by_checkout_month_and_supplier_with_hive_style_keys()
    {
        var batches = ArchiveLayout.Partition(new[]
        {
            R("acme", "r1", Date(2024, 8, 3)), R("acme", "r2", Date(2024, 8, 20)),
            R("acme", "r3", Date(2024, 9, 1)), R("other", "r1", Date(2024, 8, 5))
        });

        batches.Should().HaveCount(3);
        batches.Select(b => b.Key[..b.Key.LastIndexOf('/')]).Should().Equal(
            "reservations/v1/checkout=2024-08/supplier=acme",
            "reservations/v1/checkout=2024-08/supplier=other",
            "reservations/v1/checkout=2024-09/supplier=acme");
        batches.Should().OnlyContain(b => b.Key.EndsWith(".jsonl.gz"));
        batches[0].Reservations.Select(r => r.ReservationId).Should().Equal("r1", "r2");
    }

    [Fact]
    public void The_same_rows_always_produce_the_same_key_so_a_retry_overwrites_an_identical_object()
    {
        var rows = new[] { R("acme", "r2", Date(2024, 8, 3)), R("acme", "r1", Date(2024, 8, 3)) };

        var first = ArchiveLayout.Partition(rows).Single().Key;
        var again = ArchiveLayout.Partition(rows.Reverse()).Single().Key; // input order must not matter

        again.Should().Be(first);
    }

    [Fact]
    public void A_different_version_of_a_row_gives_a_different_key_so_history_is_never_overwritten()
    {
        var v1 = R("acme", "r1", Date(2024, 8, 3));
        var v2 = v1 with { PriceCents = 999, UpdatedAtUtc = v1.UpdatedAtUtc.AddDays(1) };

        ArchiveLayout.Partition([v1]).Single().Key.Should().NotBe(ArchiveLayout.Partition([v2]).Single().Key);
    }

    [Theory]
    [InlineData("acme", "acme")]
    [InlineData("a-b_c9", "a-b_c9")]
    [InlineData("..", "%2E%2E")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("UPPER", "%55%50%50%45%52")]
    [InlineData("a b", "a%20b")]
    [InlineData("日", "%E6%97%A5")]
    [InlineData("", "_empty")]
    public void Supplier_ids_become_one_safe_path_segment(string supplier, string expected) =>
        ArchiveLayout.EncodeSegment(supplier).Should().Be(expected);

    [Theory]
    [InlineData("reservations/v1/checkout=2024-08/supplier=acme/abc.jsonl.gz", true)]
    [InlineData("../reservations/v1/x", false)]
    [InlineData("reservations/v1/../../etc/passwd", false)]
    [InlineData("reservations/v1/./x", false)]
    [InlineData("/reservations/v1/x", false)]
    [InlineData("other/prefix/x", false)]
    [InlineData("reservations/v1/a\\b", false)]
    [InlineData("reservations/v1/a\nb", false)]
    [InlineData("", false)]
    public void Key_validation_rejects_anything_outside_the_archive_root(string key, bool valid)
    {
        var act = () => ArchiveLayout.EnsureValidKey(key);
        if (valid) act.Should().NotThrow(); else act.Should().Throw<ArgumentException>();
    }
}
