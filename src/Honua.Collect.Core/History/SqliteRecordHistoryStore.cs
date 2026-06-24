using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.History;

/// <summary>
/// An <see cref="IRecordHistoryStore"/> backed by the same SQLite database file the
/// captured records live in (BACKLOG #38 reuses the existing storage seam — no new
/// database). Each <see cref="RecordEdit"/> is appended to a dedicated
/// <c>record_edit_history</c> table keyed by <c>(record_id, sequence)</c>; the
/// field-level <see cref="FieldChange"/>s are serialized to JSON with their values
/// rendered to text, so the stored log is a stable snapshot independent of how the
/// live record happens to be typed. The primary key plus the next-sequence check
/// in <see cref="AppendAsync"/> keep the per-record sequence monotonic and gap-free,
/// so the log is tamper-evident.
/// </summary>
public sealed class SqliteRecordHistoryStore : IRecordHistoryStore
{
    private const string TableName = "record_edit_history";

    private static readonly JsonSerializerOptions ChangesJsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _connectionString;
    private readonly bool _encryptionRequested;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    /// <summary>
    /// Creates a history store over the given connection string or database file
    /// path (same conventions as <see cref="Storage.SqliteRecordStore"/>), so both
    /// stores can target one device database.
    /// </summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">Optional SQLCipher key; when set, the database must be encrypted at rest.</param>
    public SqliteRecordHistoryStore(string connectionStringOrPath, string? encryptionKey = null)
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
    public async Task AppendAsync(string recordId, RecordEdit edit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(edit);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var next = await ReadNextSequenceAsync(connection, recordId, ct).ConfigureAwait(false);
        if (edit.Sequence != next)
        {
            throw new InvalidOperationException(
                $"Edit history for record '{recordId}' expected sequence {next} but was given " +
                $"sequence {edit.Sequence}. The sequence must be monotonic and gap-free.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName}
                (record_id, sequence, timestamp_utc, editor_user_id, after_sync, note, changes_json)
            VALUES
                ($record_id, $sequence, $timestamp_utc, $editor_user_id, $after_sync, $note, $changes_json);
            """;
        command.Parameters.AddWithValue("$record_id", recordId);
        command.Parameters.AddWithValue("$sequence", edit.Sequence);
        command.Parameters.AddWithValue(
            "$timestamp_utc",
            edit.TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$editor_user_id", edit.EditorUserId);
        command.Parameters.AddWithValue("$after_sync", edit.AfterSync ? 1 : 0);
        command.Parameters.AddWithValue("$note", (object?)edit.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$changes_json", SerializeChanges(edit.Changes));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecordEdit>> GetHistoryAsync(string recordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT sequence, timestamp_utc, editor_user_id, after_sync, note, changes_json
            FROM {TableName}
            WHERE record_id = $record_id
            ORDER BY sequence ASC;
            """;
        command.Parameters.AddWithValue("$record_id", recordId);

        var edits = new List<RecordEdit>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            edits.Add(ReadEdit(reader));
        }

        return edits;
    }

    /// <inheritdoc />
    public async Task<int> GetNextSequenceAsync(string recordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await ReadNextSequenceAsync(connection, recordId, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadNextSequenceAsync(
        SqliteConnection connection,
        string recordId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {TableName} WHERE record_id = $record_id;";
        command.Parameters.AddWithValue("$record_id", recordId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static RecordEdit ReadEdit(SqliteDataReader reader)
    {
        var sequence = reader.GetInt64(0);
        var timestamp = DateTimeOffset.Parse(
            reader.GetString(1),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var editor = reader.GetString(2);
        var afterSync = reader.GetInt32(3) != 0;
        var note = reader.IsDBNull(4) ? null : reader.GetString(4);
        var changes = DeserializeChanges(reader.GetString(5));

        return new RecordEdit(sequence, timestamp, editor, changes, afterSync, note);
    }

    // FieldChange.OldValue/NewValue are loosely-typed object?; persist them as their
    // stable text form so the stored log doesn't depend on the original CLR type and
    // round-trips deterministically.
    private static string SerializeChanges(IReadOnlyList<FieldChange> changes)
    {
        var rows = changes
            .Select(c => new PersistedChange(c.FieldId, Render(c.OldValue), Render(c.NewValue)))
            .ToList();
        return JsonSerializer.Serialize(rows, ChangesJsonOptions);
    }

    private static IReadOnlyList<FieldChange> DeserializeChanges(string json)
    {
        var rows = JsonSerializer.Deserialize<List<PersistedChange>>(json, ChangesJsonOptions)
            ?? new List<PersistedChange>();
        return rows.Select(r => new FieldChange(r.FieldId, r.Old, r.New)).ToList();
    }

    private static string? Render(object? value)
        => value is null ? null : Field.FieldValues.ToText(value);

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
                "(PRAGMA cipher_version returned nothing). Refusing to open the field history database unencrypted.");
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
                    record_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    editor_user_id TEXT NOT NULL,
                    after_sync INTEGER NOT NULL,
                    note TEXT NULL,
                    changes_json TEXT NOT NULL,
                    PRIMARY KEY (record_id, sequence)
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

    private sealed record PersistedChange(string FieldId, string? Old, string? New);
}
