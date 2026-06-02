using System.ComponentModel;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

public class FormPageViewModelTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "inspection",
        Name = "Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields =
                [
                    new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text, Required = true },
                    new FormField { FieldId = "hasDamage", Label = "Damage?", Type = FormFieldType.YesNo },
                    new FormField
                    {
                        FieldId = "notes",
                        Label = "Notes",
                        Type = FormFieldType.Text,
                        Required = true,
                        VisibilityRule = new FieldVisibilityRule
                        {
                            DependsOnFieldId = "hasDamage",
                            Operator = ComparisonOperator.Equals,
                            MatchValue = true,
                        },
                    },
                ],
            },
            new FormSection
            {
                SectionId = "items",
                Label = "Items",
                Repeatable = true,
                Fields = [new FormField { FieldId = "kind", Label = "Kind", Type = FormFieldType.Text, Required = true }],
            },
        ],
    };

    private static FormPageViewModel NewPage() =>
        new(FormSession.CreateForNewRecord(Form(), "r1"));

    [Fact]
    public void Page_exposes_scalar_fields_and_repeat_groups()
    {
        var page = NewPage();

        Assert.Equal("Inspection", page.Title);
        Assert.Equal(["name", "hasDamage", "notes"], page.Fields.Select(f => f.FieldId));
        Assert.Single(page.RepeatGroups);
        Assert.Equal("Items", page.RepeatGroups[0].Label);
    }

    [Fact]
    public void Editing_a_field_recomputes_dependent_visibility_live()
    {
        var page = NewPage();
        var notes = page.Fields.Single(f => f.FieldId == "notes");
        Assert.False(notes.IsVisible);

        page.Fields.Single(f => f.FieldId == "hasDamage").Value = true;

        Assert.True(notes.IsVisible);
    }

    [Fact]
    public void CanSubmit_tracks_validation_and_raises_change_notification()
    {
        var page = NewPage();
        var changes = new List<string?>();
        page.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.False(page.CanSubmit);

        page.Fields.Single(f => f.FieldId == "name").Value = "Bridge 7";

        Assert.True(page.CanSubmit);
        Assert.Contains(nameof(FormPageViewModel.CanSubmit), changes);
    }

    [Fact]
    public void Revealed_required_field_blocks_submit_until_filled()
    {
        var page = NewPage();
        page.Fields.Single(f => f.FieldId == "name").Value = "Bridge 7";
        Assert.True(page.CanSubmit);

        page.Fields.Single(f => f.FieldId == "hasDamage").Value = true; // reveals required 'notes'
        Assert.False(page.CanSubmit);
        Assert.True(page.Fields.Single(f => f.FieldId == "notes").HasError);

        page.Fields.Single(f => f.FieldId == "notes").Value = "cracked";
        Assert.True(page.CanSubmit);
    }

    [Fact]
    public void Submit_command_transitions_record_and_raises_event()
    {
        var page = NewPage();
        page.Fields.Single(f => f.FieldId == "name").Value = "Bridge 7";

        FieldRecord? submitted = null;
        page.SubmitSucceeded += (_, r) => submitted = r;

        page.SubmitCommand.Execute(null);

        Assert.True(page.IsSubmitted);
        Assert.NotNull(submitted);
        Assert.Equal(RecordStatus.Submitted, submitted!.Status);
    }

    [Fact]
    public void Adding_a_repeat_row_then_filling_it_keeps_submit_consistent()
    {
        var page = NewPage();
        page.Fields.Single(f => f.FieldId == "name").Value = "Bridge 7";
        Assert.True(page.CanSubmit);

        var group = page.RepeatGroups[0];
        group.AddCommand.Execute(null); // adds a row with required 'kind' blank
        Assert.Single(group.Instances);
        Assert.False(page.CanSubmit);

        group.Instances[0].Fields.Single(f => f.FieldId == "kind").Value = "transformer";
        Assert.True(page.CanSubmit);

        group.Remove(group.Instances[0].InstanceId);
        Assert.Empty(group.Instances);
        Assert.True(page.CanSubmit);
    }

    [Fact]
    public void Field_value_setter_raises_property_changed()
    {
        var page = NewPage();
        var name = page.Fields.Single(f => f.FieldId == "name");
        var raised = false;
        name.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(FieldViewModel.Value);

        name.Value = "X";

        Assert.True(raised);
        Assert.Equal("X", name.Value);
    }
}
