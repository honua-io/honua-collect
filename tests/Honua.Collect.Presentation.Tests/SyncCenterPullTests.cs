using Honua.Collect.Core.Records;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Sync;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

public class SyncCenterPullTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "f",
        Name = "f",
        Sections =
        [
            new FormSection
            {
                SectionId = "s",
                Label = "s",
                Fields = [new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text }],
            },
        ],
    };

    private static CollectRecordEntry SyncedEntry(string recordId, string remoteId, string name)
    {
        var record = new FieldRecord { RecordId = recordId, FormId = "f", Status = RecordStatus.Submitted };
        record.Values["name"] = name;
        var entry = new CollectRecordEntry(record);
        entry.MarkPending();
        entry.MarkSynced(remoteId: remoteId);
        return entry;
    }

    private static PulledRecord Server(long objectId, string name)
    {
        var r = new FieldRecord { RecordId = objectId.ToString(), FormId = string.Empty, Status = RecordStatus.Submitted };
        r.Values["name"] = name;
        return new PulledRecord(objectId, r);
    }

    private static FeaturePuller Puller(params PulledRecord[] records)
        => _ => Task.FromResult(new FeatureQueryResult(true, records, null));

    [Fact]
    public async Task PullAsync_builds_conflict_list_for_diverging_features()
    {
        // local rec-1 synced as object id 11, edited to "Mine"; server still says "Theirs".
        var entries = new[] { SyncedEntry("rec-1", "11", "Mine") };
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult<string?>(null),
            Puller(Server(11, "Theirs"), Server(99, "Brand New")), Form());

        var merge = await vm.PullAsync();

        Assert.NotNull(merge);
        Assert.Single(vm.Conflicts);
        var review = vm.Conflicts[0];
        Assert.True(review.HasConflicts);
        var field = Assert.Single(review.Conflicts);
        Assert.Equal("Mine", field.LocalText);
        Assert.Equal("Theirs", field.ServerText);

        // The unmatched server feature surfaces as new-from-server.
        Assert.Single(vm.NewFromServer);
        Assert.Equal(99, vm.NewFromServer[0].ObjectId);
    }

    [Fact]
    public async Task PullAsync_returns_null_and_no_conflicts_on_query_failure()
    {
        var entries = new[] { SyncedEntry("rec-1", "11", "Mine") };
        FeaturePuller failing = _ => Task.FromResult(FeatureQueryResult.Fail("boom", 500));
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult<string?>(null), failing, Form());

        var merge = await vm.PullAsync();

        Assert.Null(merge);
        Assert.Empty(vm.Conflicts);
    }

    [Fact]
    public async Task PullAsync_no_conflicts_when_server_matches_local()
    {
        var entries = new[] { SyncedEntry("rec-1", "11", "Same") };
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult<string?>(null),
            Puller(Server(11, "Same")), Form());

        var merge = await vm.PullAsync();

        Assert.NotNull(merge);
        Assert.False(merge!.HasConflicts);
        Assert.Empty(vm.Conflicts);
        Assert.Empty(vm.NewFromServer);
    }

    [Fact]
    public void Pull_path_is_disabled_when_not_configured()
    {
        var vm = new SyncCenterViewModel(
            [SyncedEntry("rec-1", "11", "x")],
            (_, _) => Task.FromResult<string?>(null));

        Assert.False(vm.CanPull);
        Assert.False(vm.PullCommand.CanExecute(null));
    }

    private static CollectRecordEntry PendingEntry(string recordId, string name)
    {
        var record = new FieldRecord { RecordId = recordId, FormId = "f", Status = RecordStatus.Submitted };
        record.Values["name"] = name;
        var entry = new CollectRecordEntry(record);
        entry.MarkPending();
        return entry;
    }

    [Fact]
    public async Task SyncAsync_uploads_pending_records_and_marks_them_synced()
    {
        var entries = new[] { PendingEntry("a", "A"), PendingEntry("b", "B") };
        var vm = new SyncCenterViewModel(entries, (e, _) => Task.FromResult<string?>($"remote-{e.Record.RecordId}"));
        Assert.True(vm.SyncCommand.CanExecute(null));

        var synced = await vm.SyncAsync();

        Assert.Equal(2, synced);
        Assert.All(entries, e => Assert.Equal("remote-" + e.Record.RecordId, e.RemoteId));
        Assert.Empty(vm.Pending);
        Assert.False(vm.SyncCommand.CanExecute(null)); // nothing left to do
    }

    [Fact]
    public async Task SyncAsync_marks_failed_when_uploader_returns_null()
    {
        var entries = new[] { PendingEntry("a", "A") };
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult<string?>(null));

        var synced = await vm.SyncAsync();

        Assert.Equal(0, synced);
        Assert.Equal("Upload was rejected.", entries[0].LastError);
        Assert.Single(vm.Pending); // a failed record remains in the outbox
    }

    [Fact]
    public async Task SyncAsync_marks_failed_and_continues_on_a_thrown_error()
    {
        var entries = new[] { PendingEntry("a", "A"), PendingEntry("b", "B") };
        var vm = new SyncCenterViewModel(entries, (e, _) =>
            e.Record.RecordId == "a"
                ? throw new InvalidOperationException("network down")
                : Task.FromResult<string?>("remote-b"));

        var synced = await vm.SyncAsync();

        Assert.Equal(1, synced); // "b" still succeeds after "a" failed
        Assert.Equal("network down", entries[0].LastError);
        Assert.Equal("remote-b", entries[1].RemoteId);
    }

    [Fact]
    public async Task SyncAsync_lets_cancellation_propagate()
    {
        var entries = new[] { PendingEntry("a", "A") };
        var vm = new SyncCenterViewModel(entries, (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<string?>("x");
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.SyncAsync(cts.Token));
    }

    [Fact]
    public async Task SyncAsync_is_a_no_op_while_already_syncing()
    {
        var gate = new TaskCompletionSource<string?>();
        var entries = new[] { PendingEntry("a", "A") };
        var vm = new SyncCenterViewModel(entries, (_, _) => gate.Task);

        var first = vm.SyncAsync();           // enters and parks on the uploader
        var second = await vm.SyncAsync();    // re-entrancy guard returns immediately

        Assert.Equal(0, second);
        gate.SetResult("remote-a");
        Assert.Equal(1, await first);
    }
}
