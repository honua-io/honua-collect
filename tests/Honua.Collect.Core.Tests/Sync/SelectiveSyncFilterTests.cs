using Honua.Collect.Core.Records;
using Honua.Collect.Core.Sync;
using Honua.Collect.Core.Tests.TestData;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

public class SelectiveSyncFilterTests
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
                Fields = [new FormField { FieldId = "status", Label = "Status", Type = FormFieldType.Text }],
            },
        ],
    };

    private static PulledRecord Server(long objectId, params (string Key, object? Value)[] values)
    {
        var r = new FieldRecord { RecordId = objectId.ToString(), FormId = string.Empty, Status = RecordStatus.Submitted };
        foreach (var (key, value) in values)
        {
            r.Values[key] = value;
        }

        return new PulledRecord(objectId, r);
    }

    private static SelectiveSyncFilter FilterFor(LayerSyncScope scope)
        => new(new SelectiveSyncPlan([scope]), scope.LayerKey);

    // --- Pull side: a partial pull merges only matching features. ---

    [Fact]
    public void Partial_pull_skips_non_matching_server_features()
    {
        var filter = FilterFor(new LayerSyncScope
        {
            LayerKey = "trees",
            Where = SyncAttributeFilter.Parse("status = 'open'"),
        });

        var merge = new FeaturePullService().Merge(
            Form(),
            [
                Server(1, ("status", "open")),    // in scope → New
                Server(2, ("status", "closed")),  // out of scope → skipped
            ],
            new Dictionary<long, FieldRecord>(),
            filter);

        // Only the in-scope feature is classified; the out-of-scope one never
        // becomes New (so it is not inserted locally).
        Assert.Single(merge.Classifications);
        Assert.Single(merge.NewRecords);
        Assert.Equal(1, merge.NewRecords[0].ObjectId);
    }

    [Fact]
    public void Partial_pull_leaves_non_matching_local_records_untouched()
    {
        var filter = FilterFor(new LayerSyncScope
        {
            LayerKey = "trees",
            Where = SyncAttributeFilter.Parse("status = 'open'"),
        });

        // The device has a local record (object id 7) that it edited offline.
        var local = FieldRecords.Create("rec-7", status: RecordStatus.Submitted, values: ("status", "MINE-EDITED"));

        // The server's version of object 7 is out of the sync scope (status closed).
        var merge = new FeaturePullService().Merge(
            Form(),
            [Server(7, ("status", "closed"))],
            new Dictionary<long, FieldRecord> { [7] = local },
            filter);

        // Because the server feature is out of scope, it is skipped: no conflict is
        // raised against the local record, and the local record is not classified
        // at all — it stays exactly as it was.
        Assert.Empty(merge.Classifications);
        Assert.Empty(merge.Conflicts);
        Assert.False(merge.HasConflicts);
        Assert.Equal("MINE-EDITED", local.Values["status"]); // untouched
    }

    [Fact]
    public void Full_pull_without_filter_classifies_everything()
    {
        var merge = new FeaturePullService().Merge(
            Form(),
            [Server(1, ("status", "open")), Server(2, ("status", "closed"))],
            new Dictionary<long, FieldRecord>());

        Assert.Equal(2, merge.Classifications.Count);
    }

    // --- Push side: the same plan gates which local records upload. ---

    [Fact]
    public void Push_gate_selects_only_in_scope_entries_and_leaves_the_rest()
    {
        var filter = FilterFor(new LayerSyncScope
        {
            LayerKey = "trees",
            RecordIds = new HashSet<string>(["push-me"], StringComparer.Ordinal),
        });

        var inScope = new CollectRecordEntry(FieldRecords.Create("push-me"));
        var outOfScope = new CollectRecordEntry(FieldRecords.Create("leave-me"));

        var selected = filter.SelectForPush([inScope, outOfScope]);

        var only = Assert.Single(selected);
        Assert.Same(inScope, only);
        // The out-of-scope entry is simply not selected; the caller's list is intact.
        Assert.Equal(RecordSyncState.Local, outOfScope.SyncState);
    }

    [Fact]
    public void Disabled_layer_pushes_nothing()
    {
        var filter = FilterFor(new LayerSyncScope { LayerKey = "trees", Enabled = false });
        var entry = new CollectRecordEntry(FieldRecords.Create("x"));

        Assert.Empty(filter.SelectForPush([entry]));
        Assert.False(filter.LayerEnabled);
    }

    [Fact]
    public void Where_clause_flows_from_the_bound_plan()
    {
        var filter = FilterFor(new LayerSyncScope
        {
            LayerKey = "trees",
            Where = SyncAttributeFilter.Parse("priority >= 5"),
        });

        Assert.Equal("priority >= 5", filter.WhereClause);
    }
}
