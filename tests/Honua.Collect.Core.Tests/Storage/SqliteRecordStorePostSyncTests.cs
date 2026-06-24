using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Collect.Core.Tests.TestData;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Storage;

/// <summary>
/// Persistence of post-sync editability + edit-version on the record store
/// (BACKLOG #38): a re-edited synced record round-trips as a single
/// <see cref="RecordSyncState.PendingUpdate"/> row that keeps its server id and
/// version across a restart, with no duplicate insert.
/// </summary>
public sealed class SqliteRecordStorePostSyncTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRecordStore _store;

    public SqliteRecordStorePostSyncTests()
    {
        _dbPath = Path.GetTempFileName();
        _store = new SqliteRecordStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static CollectRecordEntry SyncedEntry(string id, string remoteId)
    {
        var entry = new CollectRecordEntry(FieldRecords.Create(id, status: RecordStatus.Submitted, values: ("count", 1)));
        entry.MarkPending();
        entry.MarkSynced(remoteId, new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero));
        return entry;
    }

    [Fact]
    public async Task Reedited_synced_record_persists_as_a_single_pending_update_row()
    {
        var entry = SyncedEntry("r1", "srv-7");
        await _store.SaveAsync(entry);

        // Re-open and edit the same record id, then persist again.
        entry.MarkEditedAfterSync();
        entry.Record.Values["count"] = 2;
        entry.SetVersion(1);
        await _store.SaveAsync(entry);

        // Upsert keyed by record id => one row, not a duplicate insert.
        var single = Assert.Single(await _store.LoadAllAsync());
        Assert.Equal("r1", single.Record.RecordId);
        Assert.Equal(RecordSyncState.PendingUpdate, single.SyncState);
        Assert.Equal("srv-7", single.RemoteId);            // server id preserved
        Assert.NotNull(single.LastSyncedUtc);              // synced anchor preserved
        Assert.True(single.IsServerUpdate);
        Assert.Equal(RecordBox.Outbox, single.Box);
        Assert.Equal(1, single.Version);
    }

    [Fact]
    public async Task Pending_update_state_and_version_survive_a_restart()
    {
        var entry = SyncedEntry("r1", "srv-7");
        entry.MarkEditedAfterSync();
        entry.SetVersion(3);
        await _store.SaveAsync(entry);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Fresh store over the same file = restart.
        var reopened = new SqliteRecordStore($"Data Source={_dbPath}");
        var single = Assert.Single(await reopened.LoadAllAsync());

        Assert.Equal(RecordSyncState.PendingUpdate, single.SyncState);
        Assert.Equal("srv-7", single.RemoteId);
        Assert.True(single.IsServerUpdate);
        Assert.Equal(3, single.Version);
    }
}
