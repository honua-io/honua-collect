using Honua.Collect.Core.Records;
using Honua.Collect.Core.Tests.TestData;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Records;

/// <summary>
/// Post-sync editability (BACKLOG #38): a synced record can be re-opened and edited,
/// returning to a pending state that preserves the server id so the edit uploads as
/// an update rather than a duplicate insert.
/// </summary>
public class PostSyncEditTests
{
    private static CollectRecordEntry Synced(string id, string remoteId)
    {
        var entry = new CollectRecordEntry(FieldRecords.Create(id, status: RecordStatus.Submitted));
        entry.MarkPending();
        entry.MarkSynced(remoteId, new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero));
        return entry;
    }

    [Fact]
    public void Reediting_a_synced_record_returns_a_pending_update_preserving_server_id()
    {
        var entry = Synced("r1", "srv-42");

        entry.MarkEditedAfterSync();

        Assert.Equal(RecordSyncState.PendingUpdate, entry.SyncState);
        Assert.Equal("srv-42", entry.RemoteId);
        Assert.NotNull(entry.LastSyncedUtc);
        Assert.True(entry.IsServerUpdate);
        // Awaiting an update upload, it belongs back in the Outbox, not Sent.
        Assert.Equal(RecordBox.Outbox, entry.Box);
    }

    [Fact]
    public void A_post_sync_edit_uploads_as_an_update_not_a_duplicate_insert()
    {
        var entry = Synced("r1", "srv-42");
        entry.MarkEditedAfterSync();

        // The sync engine keys an update by the preserved RemoteId; the record id is
        // unchanged so it remains the same logical record (no second insert).
        Assert.True(entry.IsServerUpdate);
        Assert.Equal("srv-42", entry.RemoteId);
        Assert.Equal("r1", entry.Record.RecordId);

        // A fresh upload completes and returns the same server id.
        entry.MarkUploading();
        entry.MarkSynced("srv-42");

        Assert.Equal(RecordSyncState.Synced, entry.SyncState);
        Assert.Equal("srv-42", entry.RemoteId);
    }

    [Fact]
    public void A_record_that_never_synced_cannot_be_edited_as_an_update()
    {
        var entry = new CollectRecordEntry(FieldRecords.Create("r1", status: RecordStatus.Submitted));
        entry.MarkPending();

        Assert.Throws<InvalidOperationException>(() => entry.MarkEditedAfterSync());
    }

    [Fact]
    public void A_synced_record_with_no_server_id_cannot_be_edited_as_an_update()
    {
        var entry = new CollectRecordEntry(FieldRecords.Create("r1", status: RecordStatus.Submitted));
        entry.MarkPending();
        entry.MarkSynced(remoteId: null);

        Assert.Throws<InvalidOperationException>(() => entry.MarkEditedAfterSync());
    }

    [Fact]
    public void Reediting_an_already_pending_update_is_idempotent()
    {
        var entry = Synced("r1", "srv-42");
        entry.MarkEditedAfterSync();

        entry.MarkEditedAfterSync();

        Assert.Equal(RecordSyncState.PendingUpdate, entry.SyncState);
        Assert.Equal("srv-42", entry.RemoteId);
    }
}
