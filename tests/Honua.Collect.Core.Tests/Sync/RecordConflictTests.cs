using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

public class RecordConflictTests
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
                    new FormField { FieldId = "tags", Label = "Tags", Type = FormFieldType.MultipleChoice },
                    new FormField { FieldId = "full", Label = "Full", Type = FormFieldType.Calculated, CalculatedExpression = "concat($name)" },
                ],
            },
        ],
    };

    private static FieldRecord Record(string id, params (string Key, object? Value)[] values)
    {
        var r = new FieldRecord { RecordId = id, FormId = "f" };
        foreach (var (key, value) in values)
        {
            r.Values[key] = value;
        }

        return r;
    }

    [Fact]
    public void No_differences_yields_no_conflicts()
    {
        var local = Record("r1", ("name", "A"), ("count", 5));
        var server = Record("r1", ("name", "A"), ("count", 5));

        var conflict = RecordConflictDetector.Detect(Form(), local, server);

        Assert.False(conflict.HasConflicts);
    }

    [Fact]
    public void Numeric_equality_ignores_representation()
    {
        // 5 (int) vs "5" (string) is not a real conflict.
        var local = Record("r1", ("count", 5));
        var server = Record("r1", ("count", "5"));

        Assert.False(RecordConflictDetector.Detect(Form(), local, server).HasConflicts);
    }

    [Fact]
    public void Blank_versus_null_is_not_a_conflict()
    {
        var local = Record("r1", ("name", ""));
        var server = Record("r1"); // name missing entirely

        Assert.False(RecordConflictDetector.Detect(Form(), local, server).HasConflicts);
    }

    [Fact]
    public void Calculated_fields_are_never_reported_as_conflicts()
    {
        var local = Record("r1", ("name", "A"), ("full", "A"));
        var server = Record("r1", ("name", "A"), ("full", "stale-different"));

        Assert.False(RecordConflictDetector.Detect(Form(), local, server).HasConflicts);
    }

    [Fact]
    public void Differing_fields_are_reported_with_both_values()
    {
        var local = Record("r1", ("name", "Local"), ("count", 1));
        var server = Record("r1", ("name", "Server"), ("count", 1));

        var conflict = RecordConflictDetector.Detect(Form(), local, server);

        var field = Assert.Single(conflict.FieldConflicts);
        Assert.Equal("name", field.FieldId);
        Assert.Equal("Name", field.Label);
        Assert.Equal("Local", field.LocalValue);
        Assert.Equal("Server", field.ServerValue);
    }

    [Fact]
    public void Multichoice_difference_is_detected_elementwise()
    {
        var local = Record("r1", ("tags", new[] { "a", "b" }));
        var server = Record("r1", ("tags", new[] { "a", "c" }));

        Assert.True(RecordConflictDetector.Detect(Form(), local, server).HasConflicts);

        var same = RecordConflictDetector.Detect(
            Form(),
            Record("r1", ("tags", new[] { "a", "b" })),
            Record("r1", ("tags", new[] { "a", "b" })));
        Assert.False(same.HasConflicts);
    }

    [Fact]
    public void Resolve_applies_per_field_choices_and_keeps_local_for_the_rest()
    {
        var local = Record("r1", ("name", "Local"), ("count", 1));
        var server = Record("r1", ("name", "Server"), ("count", 2));
        var conflict = RecordConflictDetector.Detect(Form(), local, server);

        var merged = conflict.Resolve(
            new Dictionary<string, ConflictResolution>
            {
                ["name"] = ConflictResolution.KeepLocal,
                ["count"] = ConflictResolution.KeepServer,
            });

        Assert.Equal("Local", merged.Values["name"]);
        Assert.Equal(2, merged.Values["count"]);
    }

    [Fact]
    public void ResolveAll_keeps_one_side_for_every_conflict()
    {
        var local = Record("r1", ("name", "Local"), ("count", 1));
        var server = Record("r1", ("name", "Server"), ("count", 2));
        var conflict = RecordConflictDetector.Detect(Form(), local, server);

        var merged = conflict.ResolveAll(ConflictResolution.KeepServer);

        Assert.Equal("Server", merged.Values["name"]);
        Assert.Equal(2, merged.Values["count"]);
        Assert.NotSame(local, merged); // produces a new record, doesn't mutate local
        Assert.Equal("Local", local.Values["name"]);
    }
}
