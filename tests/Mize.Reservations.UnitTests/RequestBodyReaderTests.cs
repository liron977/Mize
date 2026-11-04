using FluentAssertions;
using Mize.Reservations.Api.Endpoints;
using Xunit;

namespace Mize.Reservations.UnitTests;

public class RequestBodyReaderTests
{
    /// <summary>A forward-only stream with NO length, like a chunked request body: it hands out its bytes in pieces.</summary>
    private sealed class UnknownLengthStream(long total, int piece) : Stream
    {
        private long _sent;
        public long BytesHandedOut => _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(Math.Min(count, piece), total - _sent);
            for (var i = 0; i < n; i++) buffer[offset + i] = (byte)'x';
            _sent += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var n = (int)Math.Min(Math.Min(buffer.Length, piece), total - _sent);
            buffer.Span[..n].Fill((byte)'x');
            _sent += n;
            return ValueTask.FromResult(n);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_empty_body_is_an_empty_array_not_null() =>
        (await RequestBodyReader.ReadAtMostAsync(new MemoryStream(), 100, default)).Should().BeEmpty();

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]      // exactly at the limit is allowed
    public async Task A_body_up_to_the_limit_is_returned_whole(int size)
    {
        var data = Enumerable.Repeat((byte)'a', size).ToArray();

        (await RequestBodyReader.ReadAtMostAsync(new MemoryStream(data), 100, default)).Should().Equal(data);
    }

    [Fact]
    public async Task One_byte_over_the_limit_is_refused() =>
        (await RequestBodyReader.ReadAtMostAsync(new MemoryStream(new byte[101]), 100, default)).Should().BeNull();

    [Theory]
    [InlineData(100, 7, true)]       // chunked-style delivery in small pieces, exactly the limit
    [InlineData(101, 7, false)]      // one byte over, delivered in small pieces
    [InlineData(100, 4096, true)]
    [InlineData(5000, 1000, false)]  // several pieces, the limit is crossed part-way through
    public async Task A_body_with_no_known_length_is_capped_by_what_actually_arrives(int total, int piece, bool fits)
    {
        var result = await RequestBodyReader.ReadAtMostAsync(new UnknownLengthStream(total, piece), 100, default);

        if (fits) result.Should().HaveCount(total);
        else result.Should().BeNull();
    }

    [Fact]
    public async Task An_endless_body_is_cut_off_without_being_pulled_in()
    {
        var endless = new UnknownLengthStream(total: long.MaxValue / 2, piece: 4096);

        var result = await RequestBodyReader.ReadAtMostAsync(endless, 16 * 1024, default);

        result.Should().BeNull();
        endless.BytesHandedOut.Should().BeLessThan(16 * 1024 + 2 * 4096, "reading stops as soon as the limit is crossed, it never drains the stream");
    }

    [Fact]
    public async Task A_cancelled_read_stops_with_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await RequestBodyReader.ReadAtMostAsync(new UnknownLengthStream(1000, 10), 100, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
