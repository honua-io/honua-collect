using Honua.Collect.Core.History;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Tests.TestData;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.History;

/// <summary>
/// The durable edit log (BACKLOG #38): records each committed edit as a versioned,
/// field-level diff and advances the entry's monotonic version counter.
/// </summary>
public sealed class RecordEditLogTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRecordHistoryStore _store;
    private readonly RecordEditLog _log;

    public RecordEditLogTests()
    {
        _dbPath = Path.GetTempFileName();
        _store = new SqliteRecordHistoryStore($"Data Source={_dbPath}");
        _log = new RecordEditLog(_store);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static CollectRecordEntry SyncedEntry(string id, string remoteId, params (string Key, object? Value)[] values)
    {
        var entry = new CollectRecordEntry(FieldRecords.Create(id, status: RecordStatus.Submitted, values: values));
        entry.MarkPending();
        entry.MarkSynced(remoteId);
        return entry;
    }

    [Fact]
    public async Task Records_a_field_level_before_after_diff_and_increments_version()
    {
        var entry = SyncedEntry("r1", "srv-1", ("species", "Koa"), ("count", 3));
        Assert.Equal(0, entry.Version);

        // Re-open the synced record and edit a value.
        entry.MarkEditedAfterSync();
        var before = new Dictionary<string, object?>(entry.Record.Values);
        entry.Record.Values["count"] = 4;

        var edit = await _log.RecordEditAsync(entry, before, "soleil", note: "recount");

        Assert.NotNull(edit);
        Assert.Equal(0, edit!.Sequence);
        Assert.Equal(1, entry.Version);
        Assert.Equal("soleil", edit.EditorUserId);
        Assert.Equal("recount", edit.Note);
        Assert.True(edit.AfterSync); // edited while in PendingUpdate
        var change = Assert.Single(edit.Changes);
        Assert.Equal("count", change.FieldId);
        Assert.Equal(3, change.OldValue);
        Assert.Equal(4, change.NewValue);

        // The durable copy renders values to stable text.
        var persisted = Assert.Single(await _store.GetHistoryAsync("r1"));
        Assert.Equal("3", persisted.Changes[0].OldValue);
        Assert.Equal("4", persisted.Changes[0].NewValue);
    }

    [Fact]
    public async Task A_no_op_edit_records_nothing_and_leaves_version_unchanged()
    {
        var entry = SyncedEntry("r1", "srv-1", ("count", 3));
        var before = new Dictionary<string, object?>(entry.Record.Values);
        // No actual change: the same value is written back.
        entry.Record.Values["count"] = 3;

        var edit = await _log.RecordEditAsync(entry, before, "soleil");

        Assert.Null(edit);
        Assert.Equal(0, entry.Version);
        Assert.Empty(await _store.GetHistoryAsync("r1"));
    }

    [Fact]
    public async Task Successive_edits_produce_a_monotonic_version_sequence()
    {
        var entry = SyncedEntry("r1", "srv-1", ("count", 1));

        for (var target = 2; target <= 4; target++)
        {
            var before = new Dictionary<string, object?>(entry.Record.Values);
            entry.Record.Values["count"] = target;
            var edit = await _log.RecordEditAsync(entry, before, "soleil");
            Assert.NotNull(edit);
        }

        var history = await _store.GetHistoryAsync("r1");
        Assert.Equal(new long[] { 0, 1, 2 }, history.Select(e => e.Sequence).ToArray());
        Assert.Equal(3, entry.Version);
    }
}
