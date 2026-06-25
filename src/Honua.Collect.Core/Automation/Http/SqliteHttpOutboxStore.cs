using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Automation.Http;

/// <summary>
/// An <see cref="IHttpOutboxStore"/> backed by a single SQLite database file via
/// <c>Microsoft.Data.Sqlite</c>, sharing the encrypted-at-rest posture of
/// <see cref="Storage.SqliteRecordStore"/> and <see cref="Assignments.SqliteAssignmentStore"/>
/// (an optional SQLCipher key applied as the connection <c>Password</c>). The schema
/// is created lazily on first use, so the durable HTTP outbox survives an app restart
/// and replays queued requests on the next connectivity drain. Headers are stored as
/// a JSON object; the idempotency key is uniquely indexed so an enqueue can de-dupe.
/// </summary>
public sealed class SqliteHttpOutboxStore : IHttpOutboxStore
{
    private const string TableName = "collect_http_outbox";

    private readonly string _connectionString;
    private readonly bool _encryptionRequested;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    /// <summary>Creates a store over the given connection string or database file path.</summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">Optional SQLCipher key; when non-empty the database is encrypted at rest.</param>
    public SqliteHttpOutboxStore(string connectionStringOrPath, string? encryptionKey = null)
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
    public async Task SaveAsync(HttpOutboxEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName}
                (id, idempotency_key, method, url, headers, body, rule_name, status,
                 attempts, enqueued_utc, next_attempt_utc, last_status_code, last_error)
            VALUES
                ($id, $idempotency_key, $method, $url, $headers, $body, $rule_name, $status,
                 $attempts, $enqueued_utc, $next_attempt_utc, $last_status_code, $last_error)
            ON CONFLICT(id) DO UPDATE SET
                idempotency_key = excluded.idempotency_key,
                method = excluded.method,
                url = excluded.url,
                headers = excluded.headers,
                body = excluded.body,
                rule_name = excluded.rule_name,
                status = excluded.status,
                attempts = excluded.attempts,
                enqueued_utc = excluded.enqueued_utc,
                next_attempt_utc = excluded.next_attempt_utc,
                last_status_code = excluded.last_status_code,
                last_error = excluded.last_error;
            """;

        var request = entry.Request;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("$method", request.Method);
        command.Parameters.AddWithValue("$url", request.Url);
        command.Parameters.AddWithValue("$headers", SerializeHeaders(request.Headers));
        command.Parameters.AddWithValue("$body", (object?)request.Body ?? DBNull.Value);
        command.Parameters.AddWithValue("$rule_name", (object?)entry.RuleName ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)entry.Status);
        command.Parameters.AddWithValue("$attempts", entry.Attempts);
        command.Parameters.AddWithValue("$enqueued_utc", ToStorage(entry.EnqueuedAtUtc));
        command.Parameters.AddWithValue("$next_attempt_utc", ToStorage(entry.NextAttemptUtc));
        command.Parameters.AddWithValue("$last_status_code", entry.LastStatusCode is { } code ? code : (object)DBNull.Value);
        command.Parameters.AddWithValue("$last_error", (object?)entry.LastError ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HttpOutboxEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} FROM {TableName} ORDER BY enqueued_utc;";

        var entries = new List<HttpOutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task<HttpOutboxEntry?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} FROM {TableName} WHERE idempotency_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string SelectColumns =
        "SELECT id, idempotency_key, method, url, headers, body, rule_name, status, " +
        "attempts, enqueued_utc, next_attempt_utc, last_status_code, last_error";

    private static HttpOutboxEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Request = new HttpOutboxRequest
        {
            IdempotencyKey = reader.GetString(1),
            Method = reader.GetString(2),
            Url = reader.GetString(3),
            Headers = DeserializeHeaders(reader.IsDBNull(4) ? null : reader.GetString(4)),
            Body = reader.IsDBNull(5) ? null : reader.GetString(5),
        },
        RuleName = reader.IsDBNull(6) ? null : reader.GetString(6),
        Status = (HttpOutboxStatus)reader.GetInt32(7),
        Attempts = reader.GetInt32(8),
        EnqueuedAtUtc = FromStorage(reader.GetString(9)),
        NextAttemptUtc = FromStorage(reader.GetString(10)),
        LastStatusCode = reader.IsDBNull(11) ? null : reader.GetInt32(11),
        LastError = reader.IsDBNull(12) ? null : reader.GetString(12),
    };

    private static string SerializeHeaders(IReadOnlyDictionary<string, string> headers)
        => JsonSerializer.Serialize(headers);

    private static IReadOnlyDictionary<string, string> DeserializeHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return map is null || map.Count == 0
            ? ReadOnlyDictionary<string, string>.Empty
            : map;
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

    /// <summary>
    /// When an encryption key was supplied, fails closed unless SQLCipher is actually
    /// active, so the outbox database is never silently written in plaintext (same
    /// guard as <see cref="Storage.SqliteRecordStore"/>).
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
            cipherVersion = null;
        }

        if (string.IsNullOrWhiteSpace(cipherVersion))
        {
            throw new InvalidOperationException(
                "An encryption key was provided but SQLCipher is not active for this database " +
                "(PRAGMA cipher_version returned nothing). Refusing to open the HTTP outbox " +
                "database unencrypted.");
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
                CREATE TABLE IF NOT EXISTS {TableName} (
                    id TEXT PRIMARY KEY,
                    idempotency_key TEXT NOT NULL,
                    method TEXT NOT NULL,
                    url TEXT NOT NULL,
                    headers TEXT NULL,
                    body TEXT NULL,
                    rule_name TEXT NULL,
                    status INTEGER NOT NULL,
                    attempts INTEGER NOT NULL,
                    enqueued_utc TEXT NOT NULL,
                    next_attempt_utc TEXT NOT NULL,
                    last_status_code INTEGER NULL,
                    last_error TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText =
                $"CREATE UNIQUE INDEX IF NOT EXISTS idx_{TableName}_key ON {TableName} (idempotency_key);";
            await indexCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static string ToStorage(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset FromStorage(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static bool LooksLikeConnectionString(string value)
        => value.Contains('=', StringComparison.Ordinal);
}
