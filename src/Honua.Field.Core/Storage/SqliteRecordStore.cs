// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Field.Core.Storage;

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

    internal static readonly JsonSerializerOptions ValuesJsonOptions = new(JsonSerializerDefaults.General);

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

    /// <summary>Opens the device database and validates its key before making the store available.</summary>
    public static async Task<SqliteRecordStore> OpenEncryptedAsync(string path, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var connection = new SqliteConnectionStringBuilder { DataSource = path, Password = key };
        var store = new SqliteRecordStore(connection.ToString());
        try
        {
            await using var probe = await store.OpenAsync(ct).ConfigureAwait(false);
            return store;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB includes wrong keys.
        {
            throw new InvalidOperationException(
                "Field could not unlock the record database. Keep the app installed and contact your administrator to recover access.", ex);
        }
    }

    /// <inheritdoc />
    protected override string StoreDescription => "field database";

    /// <inheritdoc />
    public async Task SaveAsync(FieldRecordEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var photos = entry.OrdinaryPhotos;
        var expectedPhotos = entry.PersistedOrdinaryPhotos;
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = $"""
                SELECT sync_state, remote_id, published_submission_json, ordinary_photos_json
                FROM {TableName} WHERE record_id = $id;
                """;
            previous.Parameters.AddWithValue("$id", entry.Record.RecordId);
            await using var reader = await previous.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var exists = await reader.ReadAsync(ct).ConfigureAwait(false);
            ValidateOrdinaryWrite(entry, exists ? (RecordSyncState)reader.GetInt32(0) : null,
                exists && !reader.IsDBNull(1), exists && !reader.IsDBNull(2));
            FieldRecordEntry.ValidateOrdinaryPhotos(expectedPhotos, exists ? ReadOrdinaryPhotos(reader, 3) ?? [] : []);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {TableName}
                (record_id, form_id, status, assigned_user_id, lat, lon, accuracy,
                 created_utc, submitted_utc, completed_utc, values_json,
                 sync_state, remote_id, last_error, failed_attempts, last_synced_utc, version, published_submission_json, published_draft_json, ordinary_photos_json)
            VALUES
                ($record_id, $form_id, $status, $assigned_user_id, $lat, $lon, $accuracy,
                 $created_utc, $submitted_utc, $completed_utc, $values_json,
                 $sync_state, $remote_id, $last_error, $failed_attempts, $last_synced_utc, $version, $published_submission_json, $published_draft_json, $ordinary_photos_json)
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
                version = excluded.version,
                published_submission_json = excluded.published_submission_json,
                published_draft_json = excluded.published_draft_json,
                ordinary_photos_json = excluded.ordinary_photos_json;
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
        command.Parameters.AddWithValue("$published_submission_json", entry.PublishedSubmission is { } submission
            ? JsonSerializer.Serialize(submission, RecordStoreJsonContext.Default.PublishedFormSubmission)
            : DBNull.Value);

        command.Parameters.AddWithValue("$published_draft_json", entry.PublishedDraft is { } draft
            ? JsonSerializer.Serialize(draft, RecordStoreJsonContext.Default.PublishedFormDraft)
            : DBNull.Value);

        command.Parameters.AddWithValue("$ordinary_photos_json", photos.Count == 0 ? DBNull.Value
            : JsonSerializer.Serialize(photos.ToArray(), RecordStoreJsonContext.Default.OrdinaryFeaturePhotoArray));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        entry.AcceptPersistedOrdinaryPhotos(photos);
    }

    private static OrdinaryFeaturePhoto[]? ReadOrdinaryPhotos(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null
            : JsonSerializer.Deserialize(reader.GetString(ordinal), RecordStoreJsonContext.Default.OrdinaryFeaturePhotoArray);

    internal static void ValidateOrdinaryWrite(FieldRecordEntry entry, RecordSyncState? priorState,
        bool priorHasRemoteId, bool priorPublished)
    {
        var eligibleToDispatch = !priorHasRemoteId && !priorPublished
            && priorState is RecordSyncState.Local or RecordSyncState.Pending or RecordSyncState.AddRejected;
        var priorHeld = !priorHasRemoteId && !priorPublished
            && priorState is RecordSyncState.AddNeedsVerification or RecordSyncState.Uploading or RecordSyncState.Failed;
        var finishingDispatch = priorState == RecordSyncState.AddNeedsVerification && entry.PublishedSubmission is null
            && (entry.SyncState == RecordSyncState.AddRejected
                || (entry.SyncState == RecordSyncState.Synced && entry.RemoteId is not null));

        if ((entry.SyncState == RecordSyncState.AddNeedsVerification && !eligibleToDispatch)
            || (priorHeld && !finishingDispatch)
            || (priorHasRemoteId && entry.IsOrdinaryAdd))
        {
            throw new InvalidOperationException("This record changed before it could be saved. Reopen it and try again.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldRecordEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT record_id, form_id, status, assigned_user_id, lat, lon, accuracy,
                   created_utc, submitted_utc, completed_utc, values_json,
                   sync_state, remote_id, last_error, failed_attempts, last_synced_utc, version, published_submission_json, published_draft_json, ordinary_photos_json
            FROM {TableName};
            """;

        var entries = new List<FieldRecordEntry>();
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

    private static FieldRecordEntry ReadEntry(SqliteDataReader reader)
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

        var submission = reader.IsDBNull(17)
            ? null
            : JsonSerializer.Deserialize(reader.GetString(17), RecordStoreJsonContext.Default.PublishedFormSubmission)
                ?? throw new JsonException("Stored published form submission cannot be null.");

        var draft = reader.IsDBNull(18)
            ? null
            : JsonSerializer.Deserialize(reader.GetString(18), RecordStoreJsonContext.Default.PublishedFormDraft)
                ?? throw new JsonException("Stored published form draft cannot be null.");
        var entry = new FieldRecordEntry(record) { PublishedSubmission = submission, PublishedDraft = draft };
        Rehydrate(entry, syncState, remoteId, lastError, failedAttempts, lastSyncedUtc);
        entry.SetVersion(version);
        entry.RestoreOrdinaryPhotos(ReadOrdinaryPhotos(reader, 19));
        return entry;
    }

    /// <summary>
    /// Reconstructs an entry and drives it through the lifecycle Mark* methods so
    /// its internal bookkeeping (error, retry count, timestamps) is consistent with
    /// the stored transport state, rather than fabricated by direct assignment.
    /// </summary>
    internal static void Rehydrate(
        FieldRecordEntry entry,
        RecordSyncState syncState,
        string? remoteId,
        string? lastError,
        int failedAttempts,
        DateTimeOffset? lastSyncedUtc)
    {
        // A retry or conflict can belong to a previously synced feature.
        // Its server identity survives every subsequent upload state.
        entry.RestoreServerIdentity(remoteId, lastSyncedUtc);

        // Local records (including drafts) never transitioned; the constructor's
        // default already models that, so leave them untouched.
        if (syncState == RecordSyncState.Local)
        {
            return;
        }

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

            case RecordSyncState.AddNeedsVerification:
            case RecordSyncState.AddRejected:
                entry.RestoreOrdinaryAddState(syncState, lastError, failedAttempts);
                break;
            case RecordSyncState.SubmissionRejected:
            case RecordSyncState.Failed:
                // Replay each recorded failure so FailedAttempts matches storage.
                var attempts = Math.Max(1, failedAttempts);
                for (var i = 0; i < attempts - 1; i++)
                {
                    entry.MarkFailed(lastError ?? "Upload failed.");
                }

                if (syncState == RecordSyncState.SubmissionRejected)
                {
                    entry.MarkSubmissionRejected(lastError ?? "The server rejected this inspection.");
                }
                else
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
    }

    /// <inheritdoc />
    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        // Serialize the column probes and ALTERs across independent store instances.
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                    version INTEGER NOT NULL DEFAULT 0,
                    published_submission_json TEXT NULL,
                    published_draft_json TEXT NULL
                );
                """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureColumnAsync(command, "version", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await EnsureColumnAsync(command, "ordinary_photos_json", "TEXT NULL", ct).ConfigureAwait(false);
        await EnsureColumnAsync(command, "published_submission_json", "TEXT NULL", ct).ConfigureAwait(false);
        await EnsureColumnAsync(command, "published_draft_json", "TEXT NULL", ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(SqliteCommand command, string column, string definition, CancellationToken ct)
    {
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{TableName}') WHERE name = '{column}';";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
        {
            command.CommandText = $"ALTER TABLE {TableName} ADD COLUMN {column} {definition};";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}

[JsonSerializable(typeof(PublishedFormSubmission))]
[JsonSerializable(typeof(PublishedFormDraft))]
[JsonSerializable(typeof(OrdinaryFeaturePhoto[]))]
internal sealed partial class RecordStoreJsonContext : JsonSerializerContext
{
}
