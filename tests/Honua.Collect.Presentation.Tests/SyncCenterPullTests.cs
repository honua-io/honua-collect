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
}
