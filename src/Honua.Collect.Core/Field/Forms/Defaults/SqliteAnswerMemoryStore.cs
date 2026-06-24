using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Field.Forms.Defaults;

/// <summary>
/// An <see cref="IAnswerMemoryStore"/> backed by the same on-device SQLite engine
/// as <see cref="Storage.SqliteRecordStore"/> — it reuses the device database file
/// rather than introducing a new store. "Last answers" live in one row per form;
/// favorites live one row per (form, name). Values are JSON, round-tripping back
/// as <see cref="JsonElement"/> exactly like captured record values, so the
/// defaults the form layer reads are shaped identically to loaded record values.
/// </summary>
public sealed class SqliteAnswerMemoryStore : IAnswerMemoryStore
{
    private const string LastTable = "collect_answer_last";
    private const string FavoritesTable = "collect_answer_favorites";

    private static readonly JsonSerializerOptions ValuesJsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _connectionString;
    private readonly bool _encryptionRequested;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    /// <summary>
    /// Creates a store over the given connection string or database file path —
    /// pass the same value used for <see cref="Storage.SqliteRecordStore"/> so
    /// remembered answers share the device database.
    /// </summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">
    /// Optional SQLCipher key, applied as the connection <c>Password</c> (PRAGMA
    /// key) when non-empty — supply the same key as the record store so the whole
    /// device database is encrypted at rest.
    /// </param>
    public SqliteAnswerMemoryStore(string connectionStringOrPath, string? encryptionKey = null)
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
    public async Task RememberLastAsync(string formId, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentNullException.ThrowIfNull(values);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {LastTable} (form_id, values_json)
            VALUES ($form_id, $values_json)
            ON CONFLICT(form_id) DO UPDATE SET values_json = excluded.values_json;
            """;
        command.Parameters.AddWithValue("$form_id", formId);
        command.Parameters.AddWithValue("$values_json", Serialize(values));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>> GetLastAsync(string formId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT values_json FROM {LastTable} WHERE form_id = $form_id;";
        command.Parameters.AddWithValue("$form_id", formId);

        var json = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return json is null ? EmptyValues : Deserialize(json);
    }

    /// <inheritdoc />
    public async Task SaveFavoriteAsync(string formId, FavoriteAnswerSet favorite, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentNullException.ThrowIfNull(favorite);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {FavoritesTable} (form_id, name, values_json)
            VALUES ($form_id, $name, $values_json)
            ON CONFLICT(form_id, name) DO UPDATE SET values_json = excluded.values_json;
            """;
        command.Parameters.AddWithValue("$form_id", formId);
        command.Parameters.AddWithValue("$name", favorite.Name);
        command.Parameters.AddWithValue("$values_json", Serialize(favorite.Values));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FavoriteAnswerSet?> GetFavoriteAsync(string formId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name, values_json FROM {FavoritesTable} WHERE form_id = $form_id AND name = $name;";
        command.Parameters.AddWithValue("$form_id", formId);
        command.Parameters.AddWithValue("$name", name);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new FavoriteAnswerSet(reader.GetString(0), Deserialize(reader.GetString(1)));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FavoriteAnswerSet>> ListFavoritesAsync(string formId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name, values_json FROM {FavoritesTable} WHERE form_id = $form_id ORDER BY name;";
        command.Parameters.AddWithValue("$form_id", formId);

        var favorites = new List<FavoriteAnswerSet>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            favorites.Add(new FavoriteAnswerSet(reader.GetString(0), Deserialize(reader.GetString(1))));
        }

        return favorites;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyValues =
        new Dictionary<string, object?>();

    private static string Serialize(IReadOnlyDictionary<string, object?> values)
        => JsonSerializer.Serialize(values, ValuesJsonOptions);

    private static IReadOnlyDictionary<string, object?> Deserialize(string json)
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(json, ValuesJsonOptions)
            ?? new Dictionary<string, object?>();

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
                "(PRAGMA cipher_version returned nothing). Refusing to store remembered answers unencrypted.");
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
                CREATE TABLE IF NOT EXISTS {LastTable} (
                    form_id TEXT PRIMARY KEY,
                    values_json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS {FavoritesTable} (
                    form_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    values_json TEXT NOT NULL,
                    PRIMARY KEY (form_id, name)
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
