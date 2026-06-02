using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Forms;

public class RepeatSectionTests
{
    // A pole inspection: scalar header fields + a repeatable "attachments" section
    // (e.g. each transformer/crossarm on the pole).
    private static FormDefinition Form() => new()
    {
        FormId = "pole",
        Name = "Pole inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "header",
                Label = "Header",
                Fields =
                [
                    new FormField { FieldId = "poleId", Label = "Pole ID", Type = FormFieldType.Text, Required = true },
                ],
            },
            new FormSection
            {
                SectionId = "attachments",
                Label = "Attachments",
                Repeatable = true,
                Fields =
                [
                    new FormField { FieldId = "kind", Label = "Kind", Type = FormFieldType.Text, Required = true },
                    new FormField { FieldId = "height", Label = "Height", Type = FormFieldType.Numeric },
                ],
            },
        ],
    };

    [Fact]
    public void Repeatable_section_becomes_a_group_not_a_flat_field()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");

        // Scalar fields only in Fields; the repeat section is a group.
        Assert.Equal(["poleId"], session.Fields.Select(f => f.FieldId));
        Assert.Single(session.RepeatGroups);
        Assert.Equal("attachments", session.GetRepeat("attachments").SectionId);
        Assert.Throws<KeyNotFoundException>(() => session.GetField("kind"));
    }

    [Fact]
    public void Rows_can_be_added_filled_and_removed()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var group = session.GetRepeat("attachments");

        var row1 = group.AddInstance();
        row1.SetValue("kind", "transformer");
        row1.SetValue("height", 9);

        var row2 = session.AddRepeatInstance("attachments");
        row2.SetValue("kind", "crossarm");

        Assert.Equal(2, group.Count);

        Assert.True(group.RemoveInstance(row1.InstanceId));
        Assert.Equal(1, group.Count);
        Assert.Equal("crossarm", group.Instances[0].GetValue("kind"));
    }

    [Fact]
    public void Empty_required_field_in_a_row_blocks_submission_with_indexed_error()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        session.SetValue("poleId", "P-1");

        var row = session.AddRepeatInstance("attachments"); // kind required, left blank

        var result = session.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.FieldId == "attachments[0].kind");

        row.SetValue("kind", "transformer");
        Assert.True(session.CanSubmit);
    }

    [Fact]
    public void A_form_with_no_rows_is_valid_when_scalar_fields_are_satisfied()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        session.SetValue("poleId", "P-1");

        Assert.True(session.CanSubmit); // zero repeat rows is allowed
    }

    [Fact]
    public void Rows_persist_into_the_record_and_rehydrate_on_open()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        session.SetValue("poleId", "P-1");
        var row = session.AddRepeatInstance("attachments");
        row.SetValue("kind", "transformer");
        row.SetValue("height", 9);

        // Validate() persists rows into the record (as a draft would be saved).
        session.Validate();

        var rows = session.Record.Repeats["attachments"];
        Assert.Single(rows);
        Assert.Equal("transformer", rows[0].Values["kind"]);

        // Re-open the saved record: the row comes back.
        var reopened = FormSession.Open(Form(), session.Record);
        var group = reopened.GetRepeat("attachments");
        Assert.Equal(1, group.Count);
        Assert.Equal("transformer", group.Instances[0].GetValue("kind"));
        Assert.Equal(9, group.Instances[0].GetValue("height"));
    }

    [Fact]
    public void Removing_all_rows_clears_the_record_value()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var row = session.AddRepeatInstance("attachments");
        row.SetValue("kind", "x");
        session.Validate();
        Assert.True(session.Record.Repeats.ContainsKey("attachments"));
        Assert.Single(session.Record.Repeats["attachments"]);

        session.GetRepeat("attachments").RemoveInstance(row.InstanceId);
        session.Validate();

        Assert.False(session.Record.Repeats.ContainsKey("attachments"));
    }
}
