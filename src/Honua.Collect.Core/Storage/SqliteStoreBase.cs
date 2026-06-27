using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Storage;

/// <summary>
/// Shared base for the device's SQLite-backed stores. It owns the connection
/// boilerplate every store previously copy-pasted — connection-string building,
/// opening, the <strong>fail-closed at-rest-encryption guard</strong>, the
/// concurrency pragmas, and the lazy one-time schema gate — so a subclass only has
/// to declare its tables/migrations in <see cref="CreateSchemaAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Centralizing the cipher guard removes the risk that one store, copying the
/// pattern, omits the check and silently writes plaintext: every store that opens
/// through this base fails closed unless SQLCipher is actually engaged when a key
/// was supplied.
/// </para>
/// <para>
/// Each opened connection sets <c>busy_timeout</c> and <c>journal_mode=WAL</c> so a
/// concurrent reader/writer (sync vs UI) waits briefly for the lock instead of
/// throwing <c>SQLITE_BUSY</c> immediately, and a writer no longer blocks readers.
/// </para>
/// </remarks>
public abstract class SqliteStoreBase
{
    /// <summary>Busy-wait (ms) before SQLite gives up on a contended lock.</summary>
    private const int BusyTimeoutMs = 5000;

    private readonly string _connectionString;
    private readonly bool _encryptionRequested;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    /// <summary>Creates a store over the given connection string or database file path.</summary>
    /// <param name="connectionStringOrPath">
    /// A SQLite connection string, or a bare path to the database file. A value that
    /// is not already a <c>key=value</c> connection string is treated as the file path.
    /// </param>
    /// <param name="encryptionKey">
    /// Optional SQLCipher key. When non-empty (and a file path was supplied), the
    /// database is encrypted at rest — the key is applied as the connection
    /// <c>Password</c> (SQLCipher's <c>PRAGMA key</c>) — and the open fails closed
    /// unless the cipher is actually engaged.
    /// </param>
    protected SqliteStoreBase(string connectionStringOrPath, string? encryptionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrPath);
        if (LooksLikeConnectionString(connectionStringOrPath))
        {
            _connectionString = connectionStringOrPath;
            return;
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = connectionStringOrPath };
        if (!string.IsNullOrEmpty(encryptionKey))
        {
            builder.Password = encryptionKey; // SQLCipher: applied as PRAGMA key on open
            _encryptionRequested = true;
        }

        _connectionString = builder.ToString();
    }

    /// <summary>
    /// A short human-readable name for this store's database, used in the
    /// fail-closed encryption error (e.g. "field database", "HTTP outbox database").
    /// </summary>
    protected abstract string StoreDescription { get; }

    /// <summary>
    /// Creates the store's tables/indexes/migrations. Called once, under a lock, on
    /// the first connection. Implementations should use <c>CREATE TABLE IF NOT
    /// EXISTS</c> (and probe-then-alter for column migrations) so it is idempotent.
    /// </summary>
    /// <param name="connection">An open connection (cipher verified, pragmas applied).</param>
    /// <param name="ct">Cancellation token.</param>
    protected abstract Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct);

    /// <summary>
    /// Opens a connection, verifies at-rest encryption, applies the concurrency
    /// pragmas, and ensures the schema exists. The caller owns disposing the result.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open, ready-to-use connection.</returns>
    protected async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await EnsureCipherEngagedAsync(connection, ct).ConfigureAwait(false);
            await ApplyConnectionPragmasAsync(connection, ct).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // busy_timeout: wait (not fail) on a contended lock; WAL: readers and a
        // writer no longer block each other. journal_mode returns a row, which a
        // non-query execution harmlessly ignores.
        command.CommandText =
            $"PRAGMA busy_timeout={BusyTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}; " +
            "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// When an encryption key was supplied, fails closed unless SQLCipher is actually
    /// active. Stock <c>Microsoft.Data.Sqlite</c> over the non-SQLCipher native bundle
    /// silently ignores the <c>Password</c> (no <c>PRAGMA key</c>), so the file would
    /// be written in plaintext. <c>PRAGMA cipher_version</c> returns the SQLCipher
    /// version when the cipher is wired in, and nothing otherwise.
    /// </summary>
    private async Task EnsureCipherEngagedAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!_encryptionRequested)
        {
            return;
        }

        string? cipherVersion = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cipher_version;";
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            cipherVersion = result as string;
        }
        catch (SqliteException)
        {
            // Stock SQLite doesn't recognize the SQLCipher-specific pragma; treat that
            // as "cipher not engaged" rather than a hard error here so the single
            // InvalidOperationException below is the one consistent failure.
            cipherVersion = null;
        }

        if (string.IsNullOrWhiteSpace(cipherVersion))
        {
            throw new InvalidOperationException(
                "An encryption key was provided but SQLCipher is not active for this database " +
                "(PRAGMA cipher_version returned nothing). The native SQLCipher bundle " +
                "(e.g. SQLitePCLRaw.bundle_e_sqlcipher) is not wired in, so the database would " +
                $"be stored in plaintext. Refusing to open the {StoreDescription} unencrypted.");
        }
    }

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await CreateSchemaAsync(connection, ct).ConfigureAwait(false);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static bool LooksLikeConnectionString(string value)
        => value.Contains('=', StringComparison.Ordinal);
}
