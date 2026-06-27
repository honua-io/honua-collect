using System.Text.Json;
using Honua.Collect.Core.Records;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Storage;

/// <summary>
/// A <see cref="IRecordStore"/> backed by a single SQLite database file via
/// <c>Microsoft.Data.Sqlite</c>. The schema is created lazily on first use, so a
/// fresh device file is usable immediately. Captured field values are serialized
/// to JSON; on load they round-trip back as <see cref="JsonElement"/> values,
/// which the form layer reads positionally.
/// </summary>
public sealed class SqliteRecordStore : SqliteStoreBase, IRecordStore
{
    private const string TableName = "collect_records";

    private static readonly JsonSerializerOptions ValuesJsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>
    /// Creates a store over the given connection string or database file path.
    /// A bare path (one that is not already a <c>Data Source=</c> connection
    /// string) is treated as the SQLite file location.
    /// </summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">
    /// Optional SQLCipher key. When non-empty, the database is encrypted at rest
    /// (the key is applied as the connection <c>Password</c>, i.e. SQLCipher's
    /// <c>PRAGMA key</c>). Null/empty opens an unencrypted database.
    /// </param>
    public SqliteRecordStore(string connectionStringOrPath, string? encryptionKey = null)
        : base(connectionStringOrPath, encryptionKey)
    {
    }

    /// <inheritdoc />
    protected override string StoreDescription => "field database";

    /// <inheritdoc />
    public async Task SaveAsync(CollectRecordEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName}
                (record_id, form_id, status, assigned_user_id, lat, lon, accuracy,
                 created_utc, submitted_utc, completed_utc, values_json,
                 sync_state, remote_id, last_error, failed_attempts, last_synced_utc, version)
            VALUES
                ($record_id, $form_id, $status, $assigned_user_id, $lat, $lon, $accuracy,
                 $created_utc, $submitted_utc, $completed_utc, $values_json,
                 $sync_state, $remote_id, $last_error, $failed_attempts, $last_synced_utc, $version)
            ON CONFLICT(record_id) DO UPDATE SET
                form_id = excluded.form_id,
                status = excluded.status,
                assigned_user_id = excluded.assigned_user_id,
                lat = excluded.lat,
                lon = excluded.lon,
                accuracy = excluded.accuracy,
                created_utc = excluded.created_utc,
                submitted_utc = excluded.submitted_utc,
                completed_utc = excluded.completed_utc,
                values_json = excluded.values_json,
                sync_state = excluded.sync_state,
                remote_id = excluded.remote_id,
                last_error = excluded.last_error,
                failed_attempts = excluded.failed_attempts,
                last_synced_utc = excluded.last_synced_utc,
                version = excluded.version;
            """;

        var record = entry.Record;
        var location = record.Location;

        command.Parameters.AddWithValue("$record_id", record.RecordId);
        command.Parameters.AddWithValue("$form_id", record.FormId);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$assigned_user_id", (object?)record.AssignedUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lat", location is null ? DBNull.Value : location.Latitude);
        command.Parameters.AddWithValue("$lon", location is null ? DBNull.Value : location.Longitude);
        command.Parameters.AddWithValue("$accuracy", location?.AccuracyMeters is { } acc ? acc : (object)DBNull.Value);
        command.Parameters.AddWithValue("$created_utc", ToStorage(record.CreatedAtUtc));
        command.Parameters.AddWithValue("$submitted_utc", ToStorage(record.SubmittedAtUtc));
        command.Parameters.AddWithValue("$completed_utc", ToStorage(record.CompletedAtUtc));
        command.Parameters.AddWithValue("$values_json", JsonSerializer.Serialize(record.Values, ValuesJsonOptions));
        command.Parameters.AddWithValue("$sync_state", (int)entry.SyncState);
        command.Parameters.AddWithValue("$remote_id", (object?)entry.RemoteId ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_error", (object?)entry.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$failed_attempts", entry.FailedAttempts);
        command.Parameters.AddWithValue("$last_synced_utc", ToStorage(entry.LastSyncedUtc));
        command.Parameters.AddWithValue("$version", entry.Version);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CollectRecordEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT record_id, form_id, status, assigned_user_id, lat, lon, accuracy,
                   created_utc, submitted_utc, completed_utc, values_json,
                   sync_state, remote_id, last_error, failed_attempts, last_synced_utc, version
            FROM {TableName};
            """;

        var entries = new List<CollectRecordEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string recordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE record_id = $record_id;";
        command.Parameters.AddWithValue("$record_id", recordId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static CollectRecordEntry ReadEntry(SqliteDataReader reader)
    {
        FieldGeoPoint? location = null;
        if (!reader.IsDBNull(4) && !reader.IsDBNull(5))
        {
            double? accuracy = reader.IsDBNull(6) ? null : reader.GetDouble(6);
            location = new FieldGeoPoint(reader.GetDouble(4), reader.GetDouble(5), accuracy);
        }

        var valuesJson = reader.GetString(10);
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(valuesJson, ValuesJsonOptions)
            ?? new Dictionary<string, object?>();

        var record = new FieldRecord
        {
            RecordId = reader.GetString(0),
            FormId = reader.GetString(1),
            Status = (RecordStatus)reader.GetInt32(2),
            AssignedUserId = reader.IsDBNull(3) ? null : reader.GetString(3),
            Location = location,
            CreatedAtUtc = FromStorage(reader, 7) ?? default,
            SubmittedAtUtc = FromStorage(reader, 8),
            CompletedAtUtc = FromStorage(reader, 9),
        };

        foreach (var pair in values)
        {
            record.Values[pair.Key] = pair.Value;
        }

        var syncState = (RecordSyncState)reader.GetInt32(11);
        var remoteId = reader.IsDBNull(12) ? null : reader.GetString(12);
        var lastError = reader.IsDBNull(13) ? null : reader.GetString(13);
        var failedAttempts = reader.GetInt32(14);
        var lastSyncedUtc = FromStorage(reader, 15);
        var version = reader.IsDBNull(16) ? 0 : reader.GetInt32(16);

        var entry = Rehydrate(record, syncState, remoteId, lastError, failedAttempts, lastSyncedUtc);
        entry.SetVersion(version);
        return entry;
    }

    /// <summary>
    /// Reconstructs an entry and drives it through the lifecycle Mark* methods so
    /// its internal bookkeeping (error, retry count, timestamps) is consistent with
    /// the stored transport state, rather than fabricated by direct assignment.
    /// </summary>
    private static CollectRecordEntry Rehydrate(
        FieldRecord record,
        RecordSyncState syncState,
        string? remoteId,
        string? lastError,
        int failedAttempts,
        DateTimeOffset? lastSyncedUtc)
    {
        // Local records (including drafts) never transitioned; the constructor's
        // default already models that, so leave them untouched.
        if (syncState == RecordSyncState.Local)
        {
            return new CollectRecordEntry(record, RecordSyncState.Local);
        }

        var entry = new CollectRecordEntry(record);
        entry.MarkPending();

        switch (syncState)
        {
            case RecordSyncState.Uploading:
                entry.MarkUploading();
                break;

            case RecordSyncState.Synced:
                entry.MarkSynced(remoteId, lastSyncedUtc);
                break;

            case RecordSyncState.PendingUpdate:
                // A synced record re-edited offline: restore the synced anchor
                // (server id + synced timestamp) and then re-open it as an update,
                // which preserves both while returning to a pending state.
                entry.MarkSynced(remoteId, lastSyncedUtc);
                entry.MarkEditedAfterSync();
                break;

            case RecordSyncState.Failed:
                // Replay each recorded failure so FailedAttempts matches storage.
                var attempts = Math.Max(1, failedAttempts);
                for (var i = 0; i < attempts; i++)
                {
                    entry.MarkFailed(lastError ?? "Upload failed.");
                }

                break;

            case RecordSyncState.Conflicted:
                // The conflict body is recomputed on the next pull; keep the record
                // out of the Outbox so a retry never re-pushes over a conflict.
                entry.RestoreConflicted();
                break;

            case RecordSyncState.Pending:
            default:
                // MarkPending already applied above.
                break;
        }

        return entry;
    }

    /// <inheritdoc />
    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {TableName} (
                    record_id TEXT PRIMARY KEY,
                    form_id TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    assigned_user_id TEXT NULL,
                    lat REAL NULL,
                    lon REAL NULL,
                    accuracy REAL NULL,
                    created_utc TEXT NULL,
                    submitted_utc TEXT NULL,
                    completed_utc TEXT NULL,
                    values_json TEXT NOT NULL,
                    sync_state INTEGER NOT NULL,
                    remote_id TEXT NULL,
                    last_error TEXT NULL,
                    failed_attempts INTEGER NOT NULL,
                    last_synced_utc TEXT NULL,
                    version INTEGER NOT NULL DEFAULT 0
                );
                """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureVersionColumnAsync(connection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the <c>version</c> column to a database created before edit-history
    /// tracking existed (BACKLOG #38). The <c>CREATE TABLE IF NOT EXISTS</c> above
    /// only applies to brand-new databases, so an existing field device's table is
    /// migrated here. SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, so the column
    /// is probed first and added only when missing.
    /// </summary>
    private static async Task EnsureVersionColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{TableName}') WHERE name = 'version';";
            var result = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result is not null and not DBNull && Convert.ToInt32(result) > 0)
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {TableName} ADD COLUMN version INTEGER NOT NULL DEFAULT 0;";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static object ToStorage(DateTimeOffset value)
        => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static object ToStorage(DateTimeOffset? value)
        => value is { } v ? ToStorage(v) : DBNull.Value;

    private static DateTimeOffset? FromStorage(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
}
