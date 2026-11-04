using Microsoft.Data.Sqlite;

namespace Mize.Reservations.Storage.Sqlite;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <param name="busyTimeoutSeconds">How long a writer waits for the database lock before giving up (HTTP 503). Default 10.</param>
    public SqliteConnectionFactory(string dbPath, int busyTimeoutSeconds = 10)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = Math.Max(1, busyTimeoutSeconds) // also used as busy timeout, so concurrent writers wait instead of failing
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
