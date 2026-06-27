using Honua.Collect.Core.Records;
using Honua.Collect.Presentation.Mvvm;
using Honua.Collect.Presentation.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

public class SyncCenterCommandTests
{
    private static CollectRecordEntry PendingEntry(string recordId)
    {
        var record = new FieldRecord { RecordId = recordId, FormId = "f", Status = RecordStatus.Submitted };
        record.Values["name"] = "v";
        var entry = new CollectRecordEntry(record);
        entry.MarkPending();
        return entry;
    }

    [Fact]
    public async Task SyncCommand_is_disabled_the_moment_a_sync_starts()
    {
        var gate = new TaskCompletionSource<string?>();
        var entries = new[] { PendingEntry("r1") };
        var vm = new SyncCenterViewModel(entries, (_, _) => gate.Task);

        Assert.True(vm.SyncCommand.CanExecute(null)); // pending work, not syncing

        var canExecuteChanges = 0;
        vm.SyncCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        var run = vm.SyncAsync();

        // The sync is in flight (the uploader is awaiting the gate): the command
        // must already report disabled, and CanExecuteChanged must have fired so
        // a bound button is greyed out immediately rather than staying tappable.
        Assert.True(vm.IsSyncing);
        Assert.False(vm.SyncCommand.CanExecute(null));
        Assert.True(canExecuteChanges >= 1);

        gate.SetResult("remote-1");
        await run;

        Assert.False(vm.IsSyncing);
    }

    [Fact]
    public async Task A_successful_sync_persists_the_synced_entry_state()
    {
        // The upload mutates the entry to Synced; that state must be persisted so a
        // restart does not reload it as Pending and re-upload (duplicating the
        // server feature). AUD-214.
        var persisted = new List<(string Id, RecordSyncState State)>();
        var entry = PendingEntry("r1");
        var vm = new SyncCenterViewModel(
            new[] { entry },
            (_, _) => Task.FromResult<string?>("remote-1"),
            puller: null,
            form: null,
            persist: e => { persisted.Add((e.Record.RecordId, e.SyncState)); return Task.CompletedTask; });

        await vm.SyncAsync();

        Assert.Equal(RecordSyncState.Synced, entry.SyncState);
        var saved = Assert.Single(persisted);
        Assert.Equal("r1", saved.Id);
        Assert.Equal(RecordSyncState.Synced, saved.State); // durable state reflects the upload
    }

    [Fact]
    public async Task A_persistence_failure_does_not_downgrade_a_synced_record()
    {
        // If the local durable write fails after the server already accepted the
        // upload, the in-memory Synced state must stand: flipping it back to a
        // re-uploadable state would risk a duplicate on the next pass.
        var entry = PendingEntry("r1");
        var vm = new SyncCenterViewModel(
            new[] { entry },
            (_, _) => Task.FromResult<string?>("remote-1"),
            puller: null,
            form: null,
            persist: _ => throw new InvalidOperationException("disk full"));

        var synced = await vm.SyncAsync();

        Assert.Equal(1, synced);
        Assert.Equal(RecordSyncState.Synced, entry.SyncState);
    }

    [Fact]
    public async Task A_second_sync_while_one_is_running_is_a_no_op()
    {
        var gate = new TaskCompletionSource<string?>();
        var calls = 0;
        var entries = new[] { PendingEntry("r1") };
        var vm = new SyncCenterViewModel(entries, (_, _) => { calls++; return gate.Task; });

        var first = vm.SyncAsync();
        var second = await vm.SyncAsync(); // re-entrant call returns immediately

        Assert.Equal(0, second);

        gate.SetResult("remote-1");
        await first;

        Assert.Equal(1, calls); // the uploader ran exactly once
    }
}
