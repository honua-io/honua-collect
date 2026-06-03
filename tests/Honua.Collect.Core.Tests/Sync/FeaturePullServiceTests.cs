using Honua.Collect.Core.Sync;
using Honua.Collect.Core.Tests.TestData;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

public class FeaturePullServiceTests
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
                Fields =
                [
                    new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text },
                    new FormField { FieldId = "count", Label = "Count", Type = FormFieldType.Numeric },
                ],
            },
        ],
    };

    private static FieldRecord Local(string id, params (string Key, object? Value)[] values)
        => FieldRecords.Create(id, status: RecordStatus.Submitted, values: values);

    private static PulledRecord Server(long objectId, params (string Key, object? Value)[] values)
    {
        var r = new FieldRecord { RecordId = objectId.ToString(), FormId = string.Empty, Status = RecordStatus.Submitted };
        foreach (var (key, value) in values)
        {
            r.Values[key] = value;
        }

        return new PulledRecord(objectId, r);
    }

    [Fact]
    public void Unmatched_server_feature_is_new()
    {
        var merge = new FeaturePullService().Merge(
            Form(),
            [Server(11, ("name", "Alpha"), ("count", 3L))],
            new Dictionary<long, FieldRecord>());

        Assert.Single(merge.NewRecords);
        Assert.Empty(merge.Conflicts);
        Assert.Equal(PullDisposition.New, merge.Classifications[0].Disposition);
        Assert.Equal(11, merge.NewRecords[0].ObjectId);
    }

    [Fact]
    public void Matching_unchanged_feature_is_unchanged()
    {
        var local = Local("rec-1", ("name", "Alpha"), ("count", 3));
        var merge = new FeaturePullService().Merge(
            Form(),
            [Server(11, ("name", "Alpha"), ("count", 3L))],   // int 3 vs long 3 must not be a conflict
            new Dictionary<long, FieldRecord> { [11] = local });

        Assert.Single(merge.Unchanged);
        Assert.Empty(merge.Conflicts);
        Assert.Empty(merge.NewRecords);
        Assert.Equal(PullDisposition.Unchanged, merge.Classifications[0].Disposition);
    }

    [Fact]
    public void Local_edit_diverging_from_server_is_conflict()
    {
        var local = Local("rec-1", ("name", "Alpha-EDITED"), ("count", 3));
        var merge = new FeaturePullService().Merge(
            Form(),
            [Server(11, ("name", "Alpha"), ("count", 3L))],
            new Dictionary<long, FieldRecord> { [11] = local });

        Assert.True(merge.HasConflicts);
        Assert.Single(merge.Conflicts);
        Assert.Empty(merge.NewRecords);

        var conflict = merge.Conflicts[0];
        Assert.Equal("rec-1", conflict.RecordId); // keyed by the local record id, not the object id
        var field = Assert.Single(conflict.FieldConflicts);
        Assert.Equal("name", field.FieldId);
        Assert.Equal("Alpha-EDITED", field.LocalValue);
        Assert.Equal("Alpha", field.ServerValue);
    }

    [Fact]
    public void Mixed_set_classifies_each_feature_independently()
    {
        var localUnchanged = Local("u", ("name", "Same"));
        var localConflict = Local("c", ("name", "Mine"));

        var merge = new FeaturePullService().Merge(
            Form(),
            [
                Server(1, ("name", "Same")),     // unchanged
                Server(2, ("name", "Theirs")),   // conflict
                Server(3, ("name", "Fresh")),    // new
            ],
            new Dictionary<long, FieldRecord> { [1] = localUnchanged, [2] = localConflict });

        Assert.Single(merge.Unchanged);
        Assert.Single(merge.Conflicts);
        Assert.Single(merge.NewRecords);
        Assert.Equal(3, merge.Classifications.Count);
    }
}
