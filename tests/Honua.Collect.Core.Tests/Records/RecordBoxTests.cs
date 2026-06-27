using Honua.Collect.Core.Records;
using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Records;

public class RecordBoxTests
{
    private static FieldRecord NewRecord(RecordStatus status = RecordStatus.Draft)
        => new() { RecordId = "r", FormId = "f", Status = status };

    private static RecordConflict BuildConflict(string localName, string serverName)
    {
        var form = new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections = [new FormSection { SectionId = "s", Label = "s", Fields =
            [
                new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text },
            ] }],
        };
        var local = new FieldRecord { RecordId = "r", FormId = "f", Status = RecordStatus.Submitted };
        local.Values["name"] = localName;
        var server = new FieldRecord { RecordId = "r", FormId = "f" };
        server.Values["name"] = serverName;
        return RecordConflictDetector.Detect(form, local, server);
    }

    [Theory]
    [InlineData(RecordStatus.Draft, RecordSyncState.Local, RecordBox.Drafts)]
    [InlineData(RecordStatus.Draft, RecordSyncState.Pending, RecordBox.Drafts)]
    [InlineData(RecordStatus.Submitted, RecordSyncState.Local, RecordBox.Outbox)]
    [InlineData(RecordStatus.Submitted, RecordSyncState.Pending, RecordBox.Outbox)]
    [InlineData(RecordStatus.Submitted, RecordSyncState.Failed, RecordBox.Outbox)]
    [InlineData(RecordStatus.ReadyToSubmit, RecordSyncState.Pending, RecordBox.Outbox)]
    [InlineData(RecordStatus.Submitted, RecordSyncState.Conflicted, RecordBox.Conflicts)]
    [InlineData(RecordStatus.Draft, RecordSyncState.Conflicted, RecordBox.Conflicts)]
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

    [Fact]
    public void Conflicted_record_leaves_outbox_for_the_conflicts_box()
    {
        var entry = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        entry.MarkPending();
        Assert.Equal(RecordBox.Outbox, entry.Box);
        Assert.True(entry.IsPendingUpload);

        entry.MarkConflicted(BuildConflict("Local", "Server"));

        Assert.Equal(RecordBox.Conflicts, entry.Box);
        Assert.Equal(RecordSyncState.Conflicted, entry.SyncState);
        Assert.True(entry.IsConflicted);
        Assert.False(entry.IsPendingUpload); // a retry must not re-push over the conflict
        Assert.NotNull(entry.Conflict);
    }

    [Fact]
    public void Applying_a_resolution_requeues_the_merged_record()
    {
        var entry = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        entry.MarkPending();
        var conflict = BuildConflict("Local", "Server");
        entry.MarkConflicted(conflict);

        var merged = conflict.ResolveAll(ConflictResolution.KeepLocal);
        entry.ApplyResolution(merged);

        Assert.Equal(RecordSyncState.Pending, entry.SyncState);
        Assert.Equal(RecordBox.Outbox, entry.Box);
        Assert.True(entry.IsPendingUpload);
        Assert.Null(entry.Conflict);
        Assert.Equal("Local", entry.Record.Values["name"]);
    }

    [Fact]
    public void Resolving_a_conflict_on_a_synced_record_requeues_as_an_update_not_a_duplicate_add()
    {
        // The record already exists on the server (it carries a RemoteId), so the
        // resolved version must upload as an UPDATE against that object id. If it
        // re-queued as a plain Pending add it would duplicate the server feature.
        var entry = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        entry.MarkPending();
        entry.MarkSynced("srv-77");
        var conflict = BuildConflict("Local", "Server");
        entry.MarkConflicted(conflict);

        var merged = conflict.ResolveAll(ConflictResolution.KeepLocal);
        entry.ApplyResolution(merged);

        Assert.Equal(RecordSyncState.PendingUpdate, entry.SyncState);
        Assert.True(entry.IsServerUpdate);          // routes to UpdateAsync, not SubmitAsync
        Assert.Equal("srv-77", entry.RemoteId);     // server id preserved
        Assert.Equal(RecordBox.Outbox, entry.Box);
        Assert.True(entry.IsPendingUpload);
        Assert.Null(entry.Conflict);
    }

    [Fact]
    public void Applying_a_resolution_to_a_non_conflicted_record_throws()
    {
        var entry = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        entry.MarkPending();
        var merged = NewRecord(RecordStatus.Submitted);

        Assert.Throws<InvalidOperationException>(() => entry.ApplyResolution(merged));
    }

    [Fact]
    public void Summary_counts_conflicts_separately()
    {
        var conflicted = new CollectRecordEntry(NewRecord(RecordStatus.Submitted));
        conflicted.MarkPending();
        conflicted.MarkConflicted(BuildConflict("Local", "Server"));

        var entries = new[]
        {
            new CollectRecordEntry(NewRecord()),                       // Drafts
            new CollectRecordEntry(NewRecord(RecordStatus.Submitted)), // Outbox
            conflicted,                                                 // Conflicts
        };

        var summary = SyncSummary.From(entries);

        Assert.Equal(1, summary.Drafts);
        Assert.Equal(1, summary.Outbox);
        Assert.Equal(1, summary.Conflicts);
        Assert.Equal(0, summary.Sent);
        Assert.Equal(3, summary.Total);
        Assert.True(summary.HasConflicts);
    }
}
