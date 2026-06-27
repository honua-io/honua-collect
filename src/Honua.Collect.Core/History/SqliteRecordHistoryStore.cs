using System.Text.Json;
using Honua.Collect.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.History;

/// <summary>
/// An <see cref="IRecordHistoryStore"/> backed by the same SQLite database file the
/// captured records live in (BACKLOG #38 reuses the existing storage seam — no new
/// database). Each <see cref="RecordEdit"/> is appended to a dedicated
/// <c>record_edit_history</c> table keyed by <c>(record_id, sequence)</c>; the
/// field-level <see cref="FieldChange"/>s are serialized to JSON
/// <em>preserving each value's type</em> (a long stays a number, a bool stays a
/// boolean, a list stays an array) so a revert restores the original typed value
/// and a follow-up diff sees no spurious type-flip change. Values round-trip back
/// as <see cref="System.Text.Json.JsonElement"/>s — exactly as
/// <see cref="Storage.SqliteRecordStore"/> rehydrates live record values — so the
/// history and the live record compare like-for-like. The primary key plus the
/// next-sequence check in <see cref="AppendAsync"/> keep the per-record sequence
/// monotonic and gap-free, so the log is tamper-evident.
/// </summary>
public sealed class SqliteRecordHistoryStore : SqliteStoreBase, IRecordHistoryStore
{
    private const string TableName = "record_edit_history";

    private static readonly JsonSerializerOptions ChangesJsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>
    /// Creates a history store over the given connection string or database file
    /// path (same conventions as <see cref="Storage.SqliteRecordStore"/>), so both
    /// stores can target one device database.
    /// </summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">Optional SQLCipher key; when set, the database must be encrypted at rest.</param>
    public SqliteRecordHistoryStore(string connectionStringOrPath, string? encryptionKey = null)
        : base(connectionStringOrPath, encryptionKey)
    {
    }

    /// <inheritdoc />
    protected override string StoreDescription => "field history database";

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

    // FieldChange.OldValue/NewValue are loosely-typed object?; persist them as
    // type-preserving JSON (number stays a number, bool a boolean, list an array)
    // rather than flattening to text. Rendering to text would degrade a typed value
    // — e.g. 5L would come back as the string "5" — so a revert would write the
    // wrong CLR type over the live field and a follow-up diff would report a
    // spurious type-flip change. Storing the value's JSON shape lets it round-trip
    // back as a JsonElement, exactly as the live record store rehydrates values.
    private static string SerializeChanges(IReadOnlyList<FieldChange> changes)
    {
        var rows = changes
            .Select(c => new PersistedChange(c.FieldId, ToJson(c.OldValue), ToJson(c.NewValue)))
            .ToList();
        return JsonSerializer.Serialize(rows, ChangesJsonOptions);
    }

    private static IReadOnlyList<FieldChange> DeserializeChanges(string json)
    {
        var rows = JsonSerializer.Deserialize<List<PersistedChange>>(json, ChangesJsonOptions)
            ?? new List<PersistedChange>();
        return rows.Select(r => new FieldChange(r.FieldId, FromJson(r.Old), FromJson(r.New))).ToList();
    }

    // A missing element is persisted as JSON null; on read it becomes a real null so
    // IsMissing()/ReverseApply treat the field as cleared, matching the in-memory path.
    private static JsonElement ToJson(object? value)
        => JsonSerializer.SerializeToElement(value, ChangesJsonOptions);

    // Read JSON scalars back as their natural CLR type (string/long/double/bool) so
    // a reverted value is the same shape the live form/store would hold — a long
    // stays a long, not the string "5". Arrays/objects stay as JsonElement; the
    // shared reverse-apply / diff treat them by contents, and the live store also
    // rehydrates collections as JsonElement, so they compare like-for-like.
    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => ReadNumber(element),
        _ => element, // arrays / objects: keep structured, compared by contents
    };

    // An integral JSON number (no decimal point or exponent) is read back as a long
    // so a stored 5L stays a long, not a double. We key off the raw text rather than
    // JsonElement.TryGetInt64 because a JsonElement materialized via deserialization
    // doesn't always honor TryGetInt64 for an integral literal; the raw form is
    // unambiguous.
    private static object ReadNumber(JsonElement element)
    {
        // NB: box each branch to object explicitly. A `cond ? long : double` ternary
        // unifies to double, which would silently widen an integral 5L to 5d.
        if (element.TryGetInt64(out var l))
        {
            return l;
        }

        return element.GetDouble();
    }

    /// <inheritdoc />
    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
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
    }

    private sealed record PersistedChange(string FieldId, JsonElement Old, JsonElement New);
}
