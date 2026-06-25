using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Field.Forms.Localization;

/// <summary>
/// An <see cref="ILocaleStore"/> backed by the same on-device SQLite engine as
/// <see cref="Storage.SqliteRecordStore"/> — it reuses the device database file
/// rather than introducing a new store. The active language lives in one row per
/// form id, so re-opening a form restores the language the user last picked
/// (BACKLOG F2). The schema is created lazily on first use.
/// </summary>
public sealed class SqliteLocaleStore : ILocaleStore
{
    private const string Table = "collect_form_locale";

    private readonly string _connectionString;
    private readonly bool _encryptionRequested;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    /// <summary>
    /// Creates a store over the given connection string or database file path —
    /// pass the same value used for <see cref="Storage.SqliteRecordStore"/> so the
    /// active locale shares the device database.
    /// </summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">
    /// Optional SQLCipher key, applied as the connection <c>Password</c> (PRAGMA
    /// key) when non-empty — supply the same key as the record store so the whole
    /// device database is encrypted at rest.
    /// </param>
    public SqliteLocaleStore(string connectionStringOrPath, string? encryptionKey = null)
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
            builder.Password = encryptionKey;
            _encryptionRequested = true;
        }

        _connectionString = builder.ToString();
    }

    /// <inheritdoc />
    public async Task SetActiveLanguageAsync(string formId, string language, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {Table} (form_id, language)
            VALUES ($form_id, $language)
            ON CONFLICT(form_id) DO UPDATE SET language = excluded.language;
            """;
        command.Parameters.AddWithValue("$form_id", formId);
        command.Parameters.AddWithValue("$language", language);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetActiveLanguageAsync(string formId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT language FROM {Table} WHERE form_id = $form_id;";
        command.Parameters.AddWithValue("$form_id", formId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await EnsureCipherEngagedAsync(connection, ct).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

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
            cipherVersion = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        catch (SqliteException)
        {
            cipherVersion = null;
        }

        if (string.IsNullOrWhiteSpace(cipherVersion))
        {
            throw new InvalidOperationException(
                "An encryption key was provided but SQLCipher is not active for this database " +
                "(PRAGMA cipher_version returned nothing). Refusing to store the active locale unencrypted.");
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

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {Table} (
                    form_id TEXT PRIMARY KEY,
                    language TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
