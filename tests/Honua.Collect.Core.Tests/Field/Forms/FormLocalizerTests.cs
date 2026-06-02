using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Forms;

public class FormLocalizerTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "f",
        Name = "f",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields =
                [
                    new FormField
                    {
                        FieldId = "color",
                        Label = "Color",
                        Type = FormFieldType.SingleChoice,
                        HelpText = "Pick one",
                        Required = true,
                        Choices =
                        [
                            new FieldChoice { Value = "red", Label = "Red" },
                            new FieldChoice { Value = "blue", Label = "Blue" },
                        ],
                    },
                    new FormField { FieldId = "notes", Label = "Notes", Type = FormFieldType.Text },
                ],
            },
        ],
    };

    private static FormTranslations Spanish() => new()
    {
        Language = "es",
        SectionLabels = new Dictionary<string, string> { ["main"] = "Principal" },
        FieldLabels = new Dictionary<string, string> { ["color"] = "Color (es)" },
        FieldHelp = new Dictionary<string, string> { ["color"] = "Elige uno" },
        ChoiceLabels = new Dictionary<string, string>
        {
            [FormTranslations.ChoiceKey("color", "red")] = "Rojo",
            [FormTranslations.ChoiceKey("color", "blue")] = "Azul",
        },
    };

    [Fact]
    public void Localizes_section_field_help_and_choice_labels()
    {
        var localized = FormLocalizer.Localize(Form(), Spanish());

        var section = localized.Sections[0];
        Assert.Equal("Principal", section.Label);

        var color = section.Fields[0];
        Assert.Equal("Color (es)", color.Label);
        Assert.Equal("Elige uno", color.HelpText);
        Assert.Equal("Rojo", color.Choices[0].Label);
        Assert.Equal("Azul", color.Choices[1].Label);
    }

    [Fact]
    public void Missing_translations_fall_back_to_authored_text()
    {
        var localized = FormLocalizer.Localize(Form(), Spanish());

        // "notes" has no translation -> keeps its authored label.
        Assert.Equal("Notes", localized.Sections[0].Fields[1].Label);
    }

    [Fact]
    public void Localization_preserves_ids_types_and_validation()
    {
        var localized = FormLocalizer.Localize(Form(), Spanish());
        var color = localized.Sections[0].Fields[0];

        Assert.Equal("color", color.FieldId);
        Assert.Equal(FormFieldType.SingleChoice, color.Type);
        Assert.True(color.Required);
        Assert.Equal(["red", "blue"], color.Choices.Select(c => c.Value));
    }

    [Fact]
    public void Localized_form_validates_identically_to_the_source()
    {
        var localized = FormLocalizer.Localize(Form(), Spanish());
        var session = FormSession.CreateForNewRecord(localized, "r1");

        Assert.False(session.CanSubmit); // color still required

        session.SetValue("color", "red"); // stored value unchanged by localization
        Assert.True(session.CanSubmit);
    }

    [Fact]
    public void Empty_translation_value_does_not_override_authored_text()
    {
        var translations = new FormTranslations
        {
            Language = "es",
            FieldLabels = new Dictionary<string, string> { ["notes"] = "   " },
        };

        var localized = FormLocalizer.Localize(Form(), translations);
        Assert.Equal("Notes", localized.Sections[0].Fields[1].Label);
    }
}
