using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;

namespace Mize.Reservations.Archive.FileSystem;

/// <summary>
/// Archive on a local (or mounted) directory. Each object is written to a temporary file in the same directory, flushed to
/// disk, then atomically renamed into place, so a reader never sees a partial object and a crash never leaves one under a
/// real key. Intended for local development, single-host deployments and as the simplest reference adapter.
/// </summary>
public sealed class FileSystemReservationArchive : IReservationArchive
{
    private readonly string _root;

    public FileSystemReservationArchive(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task WriteAsync(ArchiveBatch batch, CancellationToken ct = default)
    {
        ArchiveLayout.EnsureValidKey(batch.Key);
        var path = Resolve(batch.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var bytes = ArchiveFormat.Serialize(batch.Reservations);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await file.WriteAsync(bytes, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true); // durable before the rename makes it visible
            }
            File.Move(temp, path, overwrite: true); // atomic replace: a retry overwrites an identical object
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<IReadOnlyList<Reservation>> ReadAsync(string key, CancellationToken ct = default)
    {
        ArchiveLayout.EnsureValidKey(key);
        try
        {
            return ArchiveFormat.Deserialize(await File.ReadAllBytesAsync(Resolve(key), ct));
        }
        catch (FileNotFoundException ex) { throw new KeyNotFoundException($"Archive object not found: {key}", ex); }
        catch (DirectoryNotFoundException ex) { throw new KeyNotFoundException($"Archive object not found: {key}", ex); }
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        ArchiveLayout.EnsureValidPrefix(prefix);
        if (!Directory.Exists(_root)) return Task.FromResult<IReadOnlyList<string>>([]);

        IReadOnlyList<string> keys = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(k => !k.Contains(".tmp-") && k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(keys);
    }

    /// <summary>Defence in depth on top of <see cref="ArchiveLayout.EnsureValidKey"/>: the resolved path must stay under the root.</summary>
    private string Resolve(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Archive key '{key}' resolves outside the archive root.", nameof(key));
        return full;
    }
}
