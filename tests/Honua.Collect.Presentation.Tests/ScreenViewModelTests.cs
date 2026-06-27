using Honua.Collect.Core.Assignments;
using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Assignments;
using Honua.Collect.Presentation.Geometry;
using Honua.Collect.Presentation.Sync;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

public class ScreenViewModelTests
{
    // --- Sync center ----------------------------------------------------------

    private static CollectRecordEntry Outbox(string id)
    {
        var entry = new CollectRecordEntry(new FieldRecord { RecordId = id, FormId = "f", Status = RecordStatus.Submitted });
        entry.MarkPending();
        return entry;
    }

    [Fact]
    public async Task SyncCenter_uploads_pending_and_moves_them_to_sent()
    {
        var entries = new[] { Outbox("a"), Outbox("b"), new CollectRecordEntry(new FieldRecord { RecordId = "draft", FormId = "f" }) };
        var vm = new SyncCenterViewModel(entries, (entry, _) => Task.FromResult(FeatureSyncResult.Ok(entry.Record.RecordId == "a" ? 1 : 2)));

        Assert.Equal(2, vm.Summary.Outbox);
        Assert.Equal(2, vm.Pending.Count);

        var synced = await vm.SyncAsync();

        Assert.Equal(2, synced);
        Assert.Equal(0, vm.Summary.Outbox);
        Assert.Equal(2, vm.Summary.Sent);
        Assert.Empty(vm.Pending);
    }

    [Fact]
    public async Task SyncCenter_marks_failures_and_keeps_them_pending()
    {
        var entries = new[] { Outbox("a") };
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult(FeatureSyncResult.Fail("rejected"))); // rejected

        var synced = await vm.SyncAsync();

        Assert.Equal(0, synced);
        Assert.Equal(1, vm.Summary.Failed);
        Assert.Single(vm.Pending);
    }

    // --- Conflict review ------------------------------------------------------

    private static RecordConflict Conflict()
    {
        var form = new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections = [new FormSection { SectionId = "s", Label = "s", Fields =
            [
                new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text },
                new FormField { FieldId = "count", Label = "Count", Type = FormFieldType.Numeric },
            ] }],
        };
        var local = new FieldRecord { RecordId = "r", FormId = "f" };
        local.Values["name"] = "Local";
        local.Values["count"] = 1;
        var server = new FieldRecord { RecordId = "r", FormId = "f" };
        server.Values["name"] = "Server";
        server.Values["count"] = 2;
        return RecordConflictDetector.Detect(form, local, server);
    }

    [Fact]
    public void ConflictReview_resolves_per_field_choices()
    {
        var vm = new ConflictReviewViewModel(Conflict());
        Assert.True(vm.HasConflicts);
        Assert.Equal(2, vm.Conflicts.Count);

        vm.Conflicts.Single(c => c.FieldId == "name").Resolution = ConflictResolution.KeepLocal;
        vm.Conflicts.Single(c => c.FieldId == "count").Resolution = ConflictResolution.KeepServer;

        var merged = vm.Resolve();
        Assert.Equal("Local", merged.Values["name"]);
        Assert.Equal(2, merged.Values["count"]);
    }

    [Fact]
    public void ConflictReview_bulk_keep_local_sets_all()
    {
        var vm = new ConflictReviewViewModel(Conflict());
        vm.KeepAllLocalCommand.Execute(null);

        var merged = vm.Resolve();
        Assert.Equal("Local", merged.Values["name"]);
        Assert.Equal(1, merged.Values["count"]);
        Assert.All(vm.Conflicts, c => Assert.True(c.KeepLocal));
    }

    [Fact]
    public void ConflictReview_unbound_cannot_apply()
    {
        var vm = new ConflictReviewViewModel(Conflict());

        Assert.False(vm.CanApply);
        Assert.False(vm.ApplyResolutionCommand.CanExecute(null));
        Assert.Throws<InvalidOperationException>(() => vm.ApplyResolution());
    }

    [Fact]
    public void ConflictReview_bound_applies_resolution_to_the_entry_and_disables_reapply()
    {
        var entry = new CollectRecordEntry(new FieldRecord { RecordId = "r", FormId = "f", Status = RecordStatus.Submitted });
        entry.MarkPending();
        entry.MarkConflicted(Conflict());

        var vm = new ConflictReviewViewModel(entry);
        Assert.True(vm.CanApply);
        Assert.True(vm.ApplyResolutionCommand.CanExecute(null));

        vm.KeepAllLocalCommand.Execute(null);
        vm.ApplyResolutionCommand.Execute(null);

        Assert.Equal(RecordSyncState.Pending, entry.SyncState);
        Assert.Equal("Local", entry.Record.Values["name"]);
        Assert.Null(entry.Conflict);
        Assert.True(vm.IsResolved);
        Assert.False(vm.ApplyResolutionCommand.CanExecute(null)); // can't re-apply
    }

    [Fact]
    public void ConflictReview_bound_to_non_conflicted_entry_throws()
    {
        var entry = new CollectRecordEntry(new FieldRecord { RecordId = "r", FormId = "f", Status = RecordStatus.Submitted });
        entry.MarkPending();

        Assert.Throws<ArgumentException>(() => new ConflictReviewViewModel(entry));
    }

    // --- Inbox ----------------------------------------------------------------

    [Fact]
    public void Inbox_lists_open_assignments_and_start_moves_them()
    {
        var due = new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero);
        var assignment = new FieldAssignment { AssignmentId = "a1", FormId = "f", AssignedToUserId = "u1", Title = "Inspect", DueAtUtc = due };
        var inbox = new AssignmentInbox("u1", [assignment]);
        var vm = new InboxViewModel(inbox, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, vm.OpenCount);
        Assert.Single(vm.Open);

        vm.Start(assignment, "rec-1");

        Assert.Equal(AssignmentStatus.InProgress, assignment.Status);
        Assert.Equal("rec-1", assignment.RecordId);
    }

    // --- Map capture ----------------------------------------------------------

    [Fact]
    public void MapCapture_builds_a_polygon_with_undo_and_completeness()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Polygon);
        Assert.False(vm.IsComplete);

        vm.AddVertex(new FieldGeoPoint(0, 0));
        vm.AddVertex(new FieldGeoPoint(0, 1));
        Assert.False(vm.IsComplete);
        vm.AddVertex(new FieldGeoPoint(1, 1));
        Assert.True(vm.IsComplete);
        Assert.Equal(3, vm.Vertices.Count);

        vm.UndoCommand.Execute(null);
        Assert.Equal(2, vm.Vertices.Count);
        Assert.False(vm.IsComplete);

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Vertices);
    }

    [Fact]
    public void MapCapture_applies_point_to_record()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);
        vm.AddVertex(new FieldGeoPoint(45.5, -122.6));

        var record = new FieldRecord { RecordId = "r", FormId = "f" };
        vm.ApplyTo(record, "geom");

        Assert.Equal(45.5, record.Location!.Latitude);
    }
}
