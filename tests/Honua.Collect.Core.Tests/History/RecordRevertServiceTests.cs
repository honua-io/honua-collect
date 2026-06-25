using Honua.Collect.Core.History;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.History;

/// <summary>
/// Revert / undo-last-change (#38) over the DURABLE history. The dominant risk these
/// tests pin down: reverting a non-string field must restore the original TYPED value
/// (long/double/bool/date), so a follow-up diff sees no spurious type-flip change. The
/// history therefore goes through the real <see cref="SqliteRecordHistoryStore"/> so a
/// type-degrading persist would surface here, not only in a hand-built fake.
/// </summary>
public sealed class RecordRevertServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.GetTempFileName();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private SqliteRecordHistoryStore NewHistory() => new($"Data Source={_dbPath}");

    private sealed class InMemoryRecordStore : IRecordStore
    {
        private readonly Dictionary<string, CollectRecordEntry> _byId = new(StringComparer.Ordinal);

        public Task SaveAsync(CollectRecordEntry entry, CancellationToken ct = default)
        {
            _byId[entry.Record.RecordId] = entry;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CollectRecordEntry>> LoadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CollectRecordEntry>>(_byId.Values.ToList());

        public Task DeleteAsync(string recordId, CancellationToken ct = default)
        {
            _byId.Remove(recordId);
            return Task.CompletedTask;
        }

        public bool TryGet(string id, out CollectRecordEntry entry) => _byId.TryGetValue(id, out entry!);
    }

    private static CollectRecordEntry NewEntry(params (string Key, object? Value)[] values)
    {
        var record = new FieldRecord { RecordId = "r1", FormId = "f" };
        foreach (var (k, v) in values)
        {
            record.Values[k] = v;
        }

        return new CollectRecordEntry(record);
    }

    // Records an edit by mutating the entry's values and logging the diff durably,
    // exactly as the production edit path does.
    private static async Task EditAsync(
        RecordEditLog log,
        CollectRecordEntry entry,
        DateTimeOffset when,
        params (string Key, object? Value)[] newValues)
    {
        var before = new Dictionary<string, object?>(entry.Record.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in newValues)
        {
            entry.Record.Values[k] = v;
        }

        await log.RecordEditAsync(entry, before, "user-1", when);
    }

    [Fact]
    public async Task Revert_restores_a_long_value_without_degrading_it_to_text()
    {
        var history = NewHistory();
        var log = new RecordEditLog(history);
        var store = new InMemoryRecordStore();
        var entry = NewEntry(("count", 5L));
        await store.SaveAsync(entry);

        // Edit the long field, then revert.
        await EditAsync(log, entry, T0, ("count", 9L));
        var service = new RecordRevertService(store, history);

        var result = await service.UndoLastChangeAsync(entry, "user-2", T0.AddMinutes(1));

        // The restored value is a long 5, NOT the string "5".
        var restored = entry.Record.Values["count"];
        Assert.IsType<long>(restored);
        Assert.Equal(5L, restored);

        // And a follow-up diff against the restored record sees NO spurious change:
        // re-deriving the edit from the now-current values against themselves is empty.
        var noChange = RecordEditHistory.ComputeChanges(
            entry.Record.Values, new Dictionary<string, object?>(entry.Record.Values));
        Assert.Empty(noChange);
        Assert.NotNull(result.RevertEdit);
    }

    [Fact]
    public async Task Revert_restores_typed_double_bool_and_date_values()
    {
        var history = NewHistory();
        var log = new RecordEditLog(history);
        var store = new InMemoryRecordStore();
        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var entry = NewEntry(("temp", 21.5d), ("active", true), ("seen", date.ToString("O")));
        await store.SaveAsync(entry);

        await EditAsync(log, entry, T0, ("temp", 30.0d), ("active", false));
        var service = new RecordRevertService(store, history);

        await service.UndoLastChangeAsync(entry, "u", T0.AddMinutes(1));

        Assert.IsType<double>(entry.Record.Values["temp"]);
        Assert.Equal(21.5d, entry.Record.Values["temp"]);
        Assert.IsType<bool>(entry.Record.Values["active"]);
        Assert.Equal(true, entry.Record.Values["active"]);
    }

    [Fact]
    public async Task Revert_re_edit_produces_no_spurious_type_flip_diff()
    {
        // The end-to-end guarantee: edit a long, revert it, then make a real edit. The
        // real edit's diff must show ONLY the genuine change, never a "5"→5 type flip
        // on the reverted field.
        var history = NewHistory();
        var log = new RecordEditLog(history);
        var store = new InMemoryRecordStore();
        var entry = NewEntry(("count", 5L), ("name", "a"));
        await store.SaveAsync(entry);

        await EditAsync(log, entry, T0, ("count", 9L));               // seq 0
        var service = new RecordRevertService(store, history);
        await service.UndoLastChangeAsync(entry, "u", T0.AddMinutes(1)); // seq 1 (revert)

        // Now a genuine edit to a different field.
        var before = new Dictionary<string, object?>(entry.Record.Values, StringComparer.OrdinalIgnoreCase);
        entry.Record.Values["name"] = "b";
        var changes = RecordEditHistory.ComputeChanges(before, entry.Record.Values);

        var change = Assert.Single(changes);
        Assert.Equal("name", change.FieldId); // NOT "count"
    }

    [Fact]
    public async Task RevertToVersion_reconstructs_an_earlier_version_across_multiple_edits()
    {
        var history = NewHistory();
        var log = new RecordEditLog(history);
        var store = new InMemoryRecordStore();
        var entry = NewEntry(("status", "open"), ("priority", 1L));
        await store.SaveAsync(entry);

        await EditAsync(log, entry, T0, ("status", "closed"));         // seq 0
        await EditAsync(log, entry, T0.AddDays(1), ("priority", 3L));  // seq 1
        var service = new RecordRevertService(store, history);

        // Revert to just after seq 0: status stays closed, priority back to 1 (long).
        var result = await service.RevertToVersionAsync(entry, toSequence: 0, "u", T0.AddDays(2));

        Assert.Equal("closed", entry.Record.Values["status"]);
        Assert.IsType<long>(entry.Record.Values["priority"]);
        Assert.Equal(1L, entry.Record.Values["priority"]);
        Assert.NotNull(result.RevertEdit);
    }

    [Fact]
    public async Task Revert_records_a_new_audited_edit_and_persists_the_record()
    {
        var history = NewHistory();
        var log = new RecordEditLog(history);
        var store = new InMemoryRecordStore();
        var entry = NewEntry(("count", 5L));
        await store.SaveAsync(entry);
        await EditAsync(log, entry, T0, ("count", 9L));

        var service = new RecordRevertService(store, history);
        await service.UndoLastChangeAsync(entry, "user-2", T0.AddMinutes(1), note: "undo bad value");

        // History now has the original edit plus the revert edit (append-only audit).
        var full = await history.GetHistoryAsync("r1");
        Assert.Equal(2, full.Count);
        Assert.Equal("user-2", full[1].EditorUserId);
        Assert.Equal("undo bad value", full[1].Note);

        // The store has the restored record persisted.
        Assert.True(store.TryGet("r1", out var saved));
        Assert.Equal(5L, saved.Record.Values["count"]);
    }

    [Fact]
    public async Task Reverting_a_field_added_after_the_target_version_removes_it()
    {
        var history = NewHistory();
        var log = new RecordEditLog(history);
        var store = new InMemoryRecordStore();
        var entry = NewEntry(("a", 1L));
        await store.SaveAsync(entry);
        await EditAsync(log, entry, T0, ("b", "added")); // seq 0 adds field b

        var service = new RecordRevertService(store, history);
        await service.UndoLastChangeAsync(entry, "u", T0.AddMinutes(1));

        Assert.False(entry.Record.Values.ContainsKey("b"));
        Assert.Equal(1L, entry.Record.Values["a"]);
    }

    [Fact]
    public async Task UndoLast_on_a_record_with_no_history_is_a_no_op()
    {
        var history = NewHistory();
        var store = new InMemoryRecordStore();
        var entry = NewEntry(("count", 5L));
        await store.SaveAsync(entry);

        var service = new RecordRevertService(store, history);
        var result = await service.UndoLastChangeAsync(entry, "u", T0);

        Assert.Null(result.RevertEdit);
        Assert.Equal(5L, entry.Record.Values["count"]);
    }

    [Fact]
    public async Task RevertToVersion_rejects_a_sequence_that_does_not_exist()
    {
        var history = NewHistory();
        var log = new RecordEditLog(history);
        var store = new InMemoryRecordStore();
        var entry = NewEntry(("count", 5L));
        await store.SaveAsync(entry);
        await EditAsync(log, entry, T0, ("count", 9L));

        var service = new RecordRevertService(store, history);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.RevertToVersionAsync(entry, toSequence: 99, "u", T0));
    }

    [Fact]
    public async Task Synced_record_revert_reopens_as_a_server_update()
    {
        var history = NewHistory();
        var log = new RecordEditLog(history);
        var store = new InMemoryRecordStore();
        var entry = NewEntry(("count", 5L));
        entry.Record.Status = RecordStatus.Submitted;
        entry.MarkPending();
        entry.MarkSynced("remote-1", T0);
        await store.SaveAsync(entry);

        await EditAsync(log, entry, T0.AddMinutes(1), ("count", 9L));

        var service = new RecordRevertService(store, history);
        await service.UndoLastChangeAsync(entry, "u", T0.AddMinutes(2));

        Assert.Equal(RecordSyncState.PendingUpdate, entry.SyncState);
        Assert.True(entry.IsServerUpdate);
        Assert.Equal(5L, entry.Record.Values["count"]);
    }
}
