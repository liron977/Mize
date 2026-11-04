namespace Mize.Reservations.Api.Endpoints;

/// <summary>Reads a request body with a hard cap, so an oversized or endless body is never buffered whole.</summary>
public static class RequestBodyReader
{
    /// <summary>Returns the bytes read, or null as soon as the body turns out to be larger than <paramref name="limit"/>.</summary>
    public static async Task<byte[]?> ReadAtMostAsync(Stream body, int limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read, total = 0;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit) return null;     // stop reading: the rest is never pulled in
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
