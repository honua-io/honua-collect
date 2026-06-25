using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Forms.Localization;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Field.Forms;

public class FormSessionLocalizationTests
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
                        Required = true,
                        Choices = [new FieldChoice { Value = "red", Label = "Red" }],
                    },
                ],
            },
        ],
    };

    private static FormLocalization Spanish()
    {
        var loc = new FormLocalization("en", new FormTranslations
        {
            Language = "es",
            SectionLabels = new Dictionary<string, string> { ["main"] = "Principal" },
            FieldLabels = new Dictionary<string, string> { ["color"] = "Color (es)" },
            ChoiceLabels = new Dictionary<string, string>
            {
                [FormTranslations.ChoiceKey("color", "red")] = "Rojo",
            },
        });
        loc.SetActiveLanguage("es");
        return loc;
    }

    [Fact]
    public void Session_surfaces_localized_text_for_the_active_language()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1", localization: Spanish());

        Assert.Equal("es", session.ActiveLanguage);
        Assert.Equal("Principal", session.Form.Sections[0].Label);

        var color = session.GetField("color");
        Assert.Equal("Color (es)", color.Field.Label);
        Assert.Equal("Rojo", color.Field.Choices[0].Label);
    }

    [Fact]
    public void Localized_session_validates_and_captures_against_unchanged_ids()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1", localization: Spanish());

        Assert.False(session.CanSubmit); // color still required after localization

        session.SetValue("color", "red"); // stored value is the authored choice value
        Assert.True(session.CanSubmit);
        Assert.Equal("red", session.Record.Values["color"]);
    }

    [Fact]
    public void Session_without_localization_presents_authored_text()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");

        Assert.Null(session.ActiveLanguage);
        Assert.Equal("Color", session.GetField("color").Field.Label);
    }
}
