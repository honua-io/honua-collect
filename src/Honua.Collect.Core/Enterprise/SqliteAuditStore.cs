using System.Globalization;
using Honua.Collect.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// A durable <see cref="IAuditStore"/> backed by SQLite (BACKLOG E3), reusing the
/// same on-device database seam as <see cref="Storage.SqliteRecordStore"/> — pass
/// the field database's connection string to keep the audit trail in the same
/// (optionally SQLCipher-encrypted) file. The <c>sequence</c> column is the primary
/// key, so an out-of-order or duplicate sequence is rejected by the database itself,
/// reinforcing the monotonic ordering the hash chain depends on.
/// </summary>
public sealed class SqliteAuditStore : SqliteStoreBase, IAuditStore
{
    private const string TableName = "collect_audit";

    /// <summary>Creates the store over a connection string or database file path.</summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">Optional SQLCipher key; applied as the connection password when non-empty.</param>
    public SqliteAuditStore(string connectionStringOrPath, string? encryptionKey = null)
        : base(connectionStringOrPath, encryptionKey)
    {
    }

    /// <inheritdoc />
    protected override string StoreDescription => "audit trail database";

    /// <inheritdoc />
    public async Task<long> HeadSequenceAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX(sequence) FROM {TableName};";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? -1L : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<string> HeadHashAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT hash FROM {TableName} ORDER BY sequence DESC LIMIT 1;";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string ?? string.Empty;
    }

    /// <inheritdoc />
    public async Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName}
                (sequence, timestamp_utc, user_id, action, record_id, details, previous_hash, hash)
            VALUES
                ($sequence, $timestamp_utc, $user_id, $action, $record_id, $details, $previous_hash, $hash);
            """;

        var e = entry.Event;
        command.Parameters.AddWithValue("$sequence", entry.Sequence);
        command.Parameters.AddWithValue("$timestamp_utc", e.TimestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$user_id", e.UserId);
        command.Parameters.AddWithValue("$action", (int)e.Action);
        command.Parameters.AddWithValue("$record_id", (object?)e.RecordId ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", (object?)e.Details ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous_hash", entry.PreviousHash);
        command.Parameters.AddWithValue("$hash", entry.Hash);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery? query = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var where = new List<string>();
        if (query?.UserId is { } userId)
        {
            where.Add("user_id = $user_id");
            command.Parameters.AddWithValue("$user_id", userId);
        }

        if (query?.Action is { } action)
        {
            where.Add("action = $action");
            command.Parameters.AddWithValue("$action", (int)action);
        }

        if (query?.SinceUtc is { } since)
        {
            where.Add("timestamp_utc >= $since");
            command.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        if (query?.UntilUtc is { } until)
        {
            where.Add("timestamp_utc < $until");
            command.Parameters.AddWithValue("$until", until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        var clause = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);
        command.CommandText = $"""
            SELECT sequence, timestamp_utc, user_id, action, record_id, details, previous_hash, hash
            FROM {TableName}{clause}
            ORDER BY sequence ASC;
            """;

        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var timestamp = DateTimeOffset.Parse(
                reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var auditEvent = new AuditEvent(
                timestamp,
                reader.GetString(2),
                (AuditAction)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
            entries.Add(new AuditEntry(reader.GetInt64(0), auditEvent, reader.GetString(6), reader.GetString(7)));
        }

        return entries;
    }

    /// <inheritdoc />
    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                sequence INTEGER PRIMARY KEY,
                timestamp_utc TEXT NOT NULL,
                user_id TEXT NOT NULL,
                action INTEGER NOT NULL,
                record_id TEXT NULL,
                details TEXT NULL,
                previous_hash TEXT NOT NULL,
                hash TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
