using Honua.Collect.Core.Records;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Records;

public class RecordBoxTests
{
    private static FieldRecord NewRecord(RecordStatus status = RecordStatus.Draft)
        => new() { RecordId = "r", FormId = "f", Status = status };

    [Theory]
    [InlineData(RecordStatus.Draft, RecordSyncState.Local, RecordBox.Drafts)]
    [InlineData(RecordStatus.Draft, RecordSyncState.Pending, RecordBox.Drafts)]
    [InlineData(RecordStatus.Submitted, RecordSyncState.Local, RecordBox.Outbox)]
    [InlineData(RecordStatus.Submitted, RecordSyncState.Pending, RecordBox.Outbox)]
    [InlineData(RecordStatus.Submitted, RecordSyncState.Failed, RecordBox.Outbox)]
    [InlineData(RecordStatus.ReadyToSubmit, RecordSyncState.Pending, RecordBox.Outbox)]
    [InlineData(RecordStatus.Submitted, RecordSyncState.Synced, RecordBox.Sent)]
    [InlineData(RecordStatus.Approved, RecordSyncState.Synced, RecordBox.Sent)]
    public void Classify_maps_status_and_sync_state_to_a_box(
        RecordStatus status, RecordSyncState syncState, RecordBox expected)
    {
        Assert.Equal(expected, RecordBoxClassifier.Classify(status, syncState));
    }

    [Fact]
    public void Draft_cannot_be_queued_for_upload()
    {
        var entry = new CollectRecordEntry(NewRecord());
        Assert.Throws<InvalidOperationException>(() => entry.MarkPending());
    }

    [Fact]
    public void Upload_lifecycle_moves_record_from_outbox_to_sent()
    {
        var entry = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        Assert.Equal(RecordBox.Outbox, entry.Box);

        entry.MarkPending();
        entry.MarkUploading();
        entry.MarkSynced(remoteId: "srv-42");

        Assert.Equal(RecordBox.Sent, entry.Box);
        Assert.Equal("srv-42", entry.RemoteId);
        Assert.Equal(RecordSyncState.Synced, entry.SyncState);
        Assert.NotNull(entry.LastSyncedUtc);
        Assert.Equal(0, entry.FailedAttempts);
    }

    [Fact]
    public void Failed_attempt_keeps_record_in_outbox_and_counts_retries()
    {
        var entry = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        entry.MarkPending();

        entry.MarkFailed("network down");
        entry.MarkFailed("network down");

        Assert.Equal(RecordBox.Outbox, entry.Box);
        Assert.Equal(RecordSyncState.Failed, entry.SyncState);
        Assert.Equal(2, entry.FailedAttempts);
        Assert.Equal("network down", entry.LastError);
    }

    [Fact]
    public void Success_after_failures_clears_error_and_resets_attempts()
    {
        var entry = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        entry.MarkPending();
        entry.MarkFailed("timeout");

        entry.MarkSynced();

        Assert.Null(entry.LastError);
        Assert.Equal(0, entry.FailedAttempts);
    }

    [Fact]
    public void Summary_aggregates_counts_across_boxes()
    {
        var synced = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        synced.MarkPending();
        synced.MarkSynced(syncedAtUtc: new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

        var failed = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        failed.MarkPending();
        failed.MarkFailed("oops");

        var entries = new[]
        {
            new CollectRecordEntry(NewRecord()),                       // Drafts
            new CollectRecordEntry(NewRecord()),                       // Drafts
            new CollectRecordEntry(NewRecord(RecordStatus.Submitted)), // Outbox (Local)
            failed,                                                     // Outbox (Failed)
            synced,                                                     // Sent
        };

        var summary = SyncSummary.From(entries);

        Assert.Equal(2, summary.Drafts);
        Assert.Equal(2, summary.Outbox);
        Assert.Equal(1, summary.Sent);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(5, summary.Total);
        Assert.True(summary.HasPendingWork);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), summary.LastSyncedUtc);
    }
}
