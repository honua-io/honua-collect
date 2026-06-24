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
