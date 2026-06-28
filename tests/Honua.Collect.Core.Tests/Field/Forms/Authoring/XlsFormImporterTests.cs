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

    [Theory]
    [InlineData("text", FormFieldType.Text)]
    [InlineData("integer", FormFieldType.Numeric)]
    [InlineData("decimal", FormFieldType.Numeric)]
    [InlineData("range", FormFieldType.Numeric)]
    [InlineData("date", FormFieldType.Date)]
    [InlineData("time", FormFieldType.Time)]
    [InlineData("datetime", FormFieldType.DateTime)]
    [InlineData("dateTime", FormFieldType.DateTime)]
    [InlineData("geopoint", FormFieldType.Location)]
    [InlineData("geotrace", FormFieldType.GeoTrace)]
    [InlineData("geoshape", FormFieldType.GeoShape)]
    [InlineData("image", FormFieldType.Photo)]
    [InlineData("audio", FormFieldType.Audio)]
    [InlineData("video", FormFieldType.Video)]
    [InlineData("barcode", FormFieldType.Barcode)]
    public void Maps_each_supported_scalar_type(string type, FormFieldType expected)
    {
        var survey = new[] { new XlsFormSurveyRow { Type = type, Name = "f", Label = "F" } };
        var form = XlsFormImporter.Import("id", "Form", survey, []);
        var field = form.Sections.SelectMany(s => s.Fields).Single();
        Assert.Equal(expected, field.Type);
    }

    [Theory]
    [InlineData("start")]      // metadata, unmapped
    [InlineData("today")]      // metadata, unmapped
    [InlineData("acknowledge")] // unsupported widget
    public void Unsupported_or_metadata_types_are_skipped(string type)
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "text", Name = "keep", Label = "Keep" },
            new XlsFormSurveyRow { Type = type, Name = "meta", Label = "Meta" },
        };

        var form = XlsFormImporter.Import("id", "Form", survey, []);
        var ids = form.Sections.SelectMany(s => s.Fields).Select(f => f.FieldId).ToList();
        Assert.Contains("keep", ids);
        Assert.DoesNotContain("meta", ids);
    }

    [Fact]
    public void Unnamed_rows_are_skipped()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "text", Name = null, Label = "no name" },
            new XlsFormSurveyRow { Type = "text", Name = "   ", Label = "blank name" },
            new XlsFormSurveyRow { Type = "text", Name = "named", Label = "Named" },
        };

        var form = XlsFormImporter.Import("id", "Form", survey, []);
        var field = Assert.Single(form.Sections.SelectMany(s => s.Fields));
        Assert.Equal("named", field.FieldId);
    }

    [Fact]
    public void Select_one_with_missing_choice_list_yields_no_choices()
    {
        var survey = new[] { new XlsFormSurveyRow { Type = "select_one absent_list", Name = "sev", Label = "Severity" } };

        var form = XlsFormImporter.Import("id", "Form", survey, []);
        var field = form.Sections.SelectMany(s => s.Fields).Single();
        Assert.Equal(FormFieldType.SingleChoice, field.Type);
        Assert.Empty(field.Choices);
    }

    [Fact]
    public void Select_multiple_resolves_choice_list()
    {
        var survey = new[] { new XlsFormSurveyRow { Type = "select_multiple colors", Name = "c", Label = "Colors" } };
        var choices = new[]
        {
            new XlsFormChoiceRow { ListName = "colors", Name = "r", Label = "Red" },
            new XlsFormChoiceRow { ListName = "colors", Name = "g", Label = "Green" },
        };

        var form = XlsFormImporter.Import("id", "Form", survey, choices);
        var field = form.Sections.SelectMany(s => s.Fields).Single();
        Assert.Equal(FormFieldType.MultipleChoice, field.Type);
        Assert.Equal(["r", "g"], field.Choices.Select(c => c.Value));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("no", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Required_token_is_interpreted(string? token, bool expected)
    {
        var survey = new[] { new XlsFormSurveyRow { Type = "text", Name = "f", Label = "F", Required = token } };
        var form = XlsFormImporter.Import("id", "Form", survey, []);
        Assert.Equal(expected, form.Sections.SelectMany(s => s.Fields).Single().Required);
    }

    [Theory]
    [InlineData("${a} = 'x'", ComparisonOperator.Equals, "x")]
    [InlineData("${a} != 'y'", ComparisonOperator.NotEquals, "y")]
    [InlineData("${a} > 3", ComparisonOperator.GreaterThan, 3d)]
    [InlineData("${a} < 9", ComparisonOperator.LessThan, 9d)]
    [InlineData("${a} = \"quoted\"", ComparisonOperator.Equals, "quoted")]
    [InlineData("${a} = bareword", ComparisonOperator.Equals, "bareword")]
    public void Relevant_parses_each_operator_and_literal(string relevant, ComparisonOperator op, object expected)
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "text", Name = "a", Label = "A" },
            new XlsFormSurveyRow { Type = "text", Name = "b", Label = "B", Relevant = relevant },
        };

        var form = XlsFormImporter.Import("id", "Form", survey, []);
        var rule = form.Sections.SelectMany(s => s.Fields).Single(f => f.FieldId == "b").VisibilityRule;
        Assert.NotNull(rule);
        Assert.Equal("a", rule!.DependsOnFieldId);
        Assert.Equal(op, rule.Operator);
        Assert.Equal(expected, rule.MatchValue);
    }

    [Theory]
    [InlineData("")]                  // whitespace-only -> no rule
    [InlineData("   ")]
    [InlineData("some_function() > 0")] // no ${...} left side -> unparsed
    [InlineData("notacomparison")]      // no operator token at all
    public void Relevant_that_is_blank_or_unparseable_yields_no_rule(string relevant)
    {
        var survey = new[] { new XlsFormSurveyRow { Type = "text", Name = "f", Label = "F", Relevant = relevant } };
        var form = XlsFormImporter.Import("id", "Form", survey, []);
        Assert.Null(form.Sections.SelectMany(s => s.Fields).Single().VisibilityRule);
    }

    [Fact]
    public void Calculate_without_expression_has_null_calculated_expression()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "calculate", Name = "c", Label = "C", Calculation = "   " },
        };

        var form = XlsFormImporter.Import("id", "Form", survey, []);
        var field = form.Sections.SelectMany(s => s.Fields).Single();
        Assert.Equal(FormFieldType.Calculated, field.Type);
        Assert.Null(field.CalculatedExpression);
    }

    [Fact]
    public void Non_calculate_field_ignores_calculation_column()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "text", Name = "t", Label = "T", Calculation = "1 + 1" },
        };

        var form = XlsFormImporter.Import("id", "Form", survey, []);
        Assert.Null(form.Sections.SelectMany(s => s.Fields).Single().CalculatedExpression);
    }

    [Fact]
    public void Hint_becomes_help_text_and_blank_hint_is_null()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "text", Name = "withHint", Label = "A", Hint = "fill this" },
            new XlsFormSurveyRow { Type = "text", Name = "noHint", Label = "B", Hint = "   " },
        };

        var fields = XlsFormImporter.Import("id", "Form", survey, [])
            .Sections.SelectMany(s => s.Fields).ToDictionary(f => f.FieldId);
        Assert.Equal("fill this", fields["withHint"].HelpText);
        Assert.Null(fields["noHint"].HelpText);
    }

    [Fact]
    public void Label_falls_back_to_name_when_absent()
    {
        var survey = new[] { new XlsFormSurveyRow { Type = "text", Name = "code", Label = null } };
        var field = XlsFormImporter.Import("id", "Form", survey, []).Sections.SelectMany(s => s.Fields).Single();
        Assert.Equal("code", field.Label);
    }

    [Fact]
    public void Group_without_a_name_gets_a_generated_section_id()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "begin group", Name = null, Label = null },
            new XlsFormSurveyRow { Type = "text", Name = "inner", Label = "Inner" },
            new XlsFormSurveyRow { Type = "end group" },
        };

        var form = XlsFormImporter.Import("id", "Form", survey, []);
        var grouped = form.Sections.Single(s => s.Fields.Any(f => f.FieldId == "inner"));
        Assert.False(string.IsNullOrWhiteSpace(grouped.SectionId));
        Assert.Equal("Section", grouped.Label); // default label
    }

    [Fact]
    public void Ungrouped_fields_are_emitted_in_a_leading_root_section()
    {
        var survey = new[]
        {
            new XlsFormSurveyRow { Type = "text", Name = "top", Label = "Top" },
            new XlsFormSurveyRow { Type = "begin group", Name = "g", Label = "G" },
            new XlsFormSurveyRow { Type = "text", Name = "inside", Label = "Inside" },
            new XlsFormSurveyRow { Type = "end group" },
        };

        var form = XlsFormImporter.Import("id", "Form", survey, []);
        Assert.Equal("form", form.Sections[0].SectionId); // root first
        Assert.Equal(["top"], form.Sections[0].Fields.Select(f => f.FieldId));
        Assert.Equal("g", form.Sections[1].SectionId);
    }

    [Fact]
    public void Validates_arguments()
    {
        Assert.Throws<ArgumentException>(() => XlsFormImporter.Import("", "n", [], []));
        Assert.Throws<ArgumentException>(() => XlsFormImporter.Import("id", "  ", [], []));
        Assert.Throws<ArgumentNullException>(() => XlsFormImporter.Import("id", "n", null!, []));
        Assert.Throws<ArgumentNullException>(() => XlsFormImporter.Import("id", "n", [], null!));
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
