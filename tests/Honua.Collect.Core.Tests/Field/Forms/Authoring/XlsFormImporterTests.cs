using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Forms.Authoring;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Field.Forms.Authoring;

public class XlsFormImporterTests
{
    [Fact]
    public void Imports_types_required_choices_relevant_and_calculation()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "text", Name = "name", Label = "Name", Required = "yes" },
            new XlsFormSurveyRow { Type = "integer", Name = "count", Label = "Count" },
            new XlsFormSurveyRow { Type = "select_one severity", Name = "sev", Label = "Severity" },
            new XlsFormSurveyRow { Type = "image", Name = "pic", Label = "Photo" },
            new XlsFormSurveyRow { Type = "barcode", Name = "code", Label = "Code" },
            new XlsFormSurveyRow
            {
                Type = "text", Name = "notes", Label = "Notes",
                Relevant = "${sev} = 'high'",
            },
            new XlsFormSurveyRow
            {
                Type = "calculate", Name = "full", Label = "Full",
                Calculation = "concat($name)",
            },
            new XlsFormSurveyRow { Type = "note", Name = "hint1", Label = "Read me" },
        };

        var choices = new[]
        {
            new XlsFormChoiceRow { ListName = "severity", Name = "low", Label = "Low" },
            new XlsFormChoiceRow { ListName = "severity", Name = "high", Label = "High" },
        };

        var form = XlsFormImporter.Import("f1", "Inspection", survey, choices);

        var fields = form.Sections.SelectMany(s => s.Fields).ToDictionary(f => f.FieldId);

        Assert.Equal(FormFieldType.Text, fields["name"].Type);
        Assert.True(fields["name"].Required);
        Assert.Equal(FormFieldType.Numeric, fields["count"].Type);
        Assert.Equal(FormFieldType.SingleChoice, fields["sev"].Type);
        Assert.Equal(["low", "high"], fields["sev"].Choices.Select(c => c.Value));
        Assert.Equal(FormFieldType.Photo, fields["pic"].Type);
        Assert.Equal(FormFieldType.Barcode, fields["code"].Type);
        Assert.Equal(FormFieldType.Calculated, fields["full"].Type);
        Assert.Equal("concat($name)", fields["full"].CalculatedExpression);

        // relevant -> visibility rule
        var notes = fields["notes"];
        Assert.NotNull(notes.VisibilityRule);
        Assert.Equal("sev", notes.VisibilityRule!.DependsOnFieldId);
        Assert.Equal(ComparisonOperator.Equals, notes.VisibilityRule.Operator);
        Assert.Equal("high", notes.VisibilityRule.MatchValue);

        // notes (xlsform 'note' type) are skipped
        Assert.False(fields.ContainsKey("hint1"));
    }

    [Fact]
    public void Groups_and_repeats_become_sections()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "begin group", Name = "header", Label = "Header" },
            new XlsFormSurveyRow { Type = "text", Name = "poleId", Label = "Pole ID" },
            new XlsFormSurveyRow { Type = "end group" },
            new XlsFormSurveyRow { Type = "begin repeat", Name = "attachments", Label = "Attachments" },
            new XlsFormSurveyRow { Type = "text", Name = "kind", Label = "Kind" },
            new XlsFormSurveyRow { Type = "end repeat" },
        };

        var form = XlsFormImporter.Import("f", "Pole", survey, []);

        var header = form.Sections.Single(s => s.SectionId == "header");
        Assert.False(header.Repeatable);
        Assert.Equal(["poleId"], header.Fields.Select(f => f.FieldId));

        var repeat = form.Sections.Single(s => s.SectionId == "attachments");
        Assert.True(repeat.Repeatable);
        Assert.Equal(["kind"], repeat.Fields.Select(f => f.FieldId));
    }

    [Fact]
    public void Relevant_parses_inequalities()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "integer", Name = "n", Label = "N" },
            new XlsFormSurveyRow { Type = "text", Name = "big", Label = "Big", Relevant = "${n} > 5" },
        };

        var form = XlsFormImporter.Import("f", "F", survey, []);
        var big = form.Sections.SelectMany(s => s.Fields).Single(f => f.FieldId == "big");

        Assert.Equal(ComparisonOperator.GreaterThan, big.VisibilityRule!.Operator);
        Assert.Equal(5d, big.VisibilityRule.MatchValue);
    }

    [Fact]
    public void Imported_form_drives_a_working_session()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "text", Name = "name", Label = "Name", Required = "yes" },
            new XlsFormSurveyRow { Type = "select_one yn", Name = "ok", Label = "OK?" },
        };
        var choices = new[]
        {
            new XlsFormChoiceRow { ListName = "yn", Name = "y", Label = "Yes" },
            new XlsFormChoiceRow { ListName = "yn", Name = "n", Label = "No" },
        };

        var form = XlsFormImporter.Import("f", "F", survey, choices);
        var session = FormSession.CreateForNewRecord(form, "r1");

        Assert.False(session.CanSubmit); // name required
        session.SetValue("name", "A");
        Assert.True(session.CanSubmit);
    }
}
