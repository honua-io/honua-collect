using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Forms.Localization;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Field.Forms;

public class FormLocalizationTests
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

    private static FormLocalization Localization() => new("en", Spanish());

    [Fact]
    public void Defaults_to_the_authored_language()
    {
        var loc = Localization();
        Assert.Equal("en", loc.ActiveLanguage);

        var form = loc.Localize(Form());
        Assert.Equal("Main", form.Sections[0].Label);
        Assert.Equal("Color", form.Sections[0].Fields[0].Label);
    }

    [Fact]
    public void Active_language_resolves_translated_label_hint_and_choice_text()
    {
        var loc = Localization();
        Assert.True(loc.SetActiveLanguage("es"));

        var form = loc.Localize(Form());
        var color = form.Sections[0].Fields[0];

        Assert.Equal("Principal", form.Sections[0].Label);
        Assert.Equal("Color (es)", color.Label);
        Assert.Equal("Elige uno", color.HelpText);
        Assert.Equal("Rojo", color.Choices[0].Label);
        Assert.Equal("Azul", color.Choices[1].Label);
    }

    [Fact]
    public void Untranslated_text_falls_back_to_the_authored_default()
    {
        var loc = Localization();
        loc.SetActiveLanguage("es");

        var form = loc.Localize(Form());
        // "notes" has no Spanish translation -> authored label survives.
        Assert.Equal("Notes", form.Sections[0].Fields[1].Label);
    }

    [Fact]
    public void Unsupported_language_falls_back_to_default_rather_than_blank()
    {
        var loc = Localization();

        Assert.False(loc.SetActiveLanguage("de")); // no German translation set
        Assert.Equal("en", loc.ActiveLanguage);

        var form = loc.Localize(Form());
        Assert.Equal("Color", form.Sections[0].Fields[0].Label);
    }

    [Fact]
    public void Available_languages_lists_default_and_translations()
    {
        var loc = Localization();
        Assert.Equal(["en", "es"], loc.AvailableLanguages);
        Assert.True(loc.SupportsLanguage("es"));
        Assert.False(loc.SupportsLanguage("fr"));
    }

    [Fact]
    public async Task Set_and_persist_round_trips_the_active_language_through_a_store()
    {
        var store = new InMemoryLocaleStore();
        var loc = Localization();

        Assert.True(await loc.SetAndPersistActiveLanguageAsync(store, "f", "es"));
        Assert.Equal("es", await store.GetActiveLanguageAsync("f"));

        // A fresh service seeds its active language from the persisted choice.
        var reopened = Localization();
        Assert.Equal("es", await reopened.SeedActiveLanguageAsync(store, "f"));
        Assert.Equal("es", reopened.ActiveLanguage);
    }

    [Fact]
    public async Task Seeding_an_unsupported_persisted_language_falls_back_to_default()
    {
        var store = new InMemoryLocaleStore();
        await store.SetActiveLanguageAsync("f", "zz"); // no longer offered

        var loc = Localization();
        Assert.Equal("en", await loc.SeedActiveLanguageAsync(store, "f"));
    }

    private sealed class InMemoryLocaleStore : ILocaleStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task SetActiveLanguageAsync(string formId, string language, CancellationToken ct = default)
        {
            _values[formId] = language;
            return Task.CompletedTask;
        }

        public Task<string?> GetActiveLanguageAsync(string formId, CancellationToken ct = default)
            => Task.FromResult(_values.TryGetValue(formId, out var v) ? v : null);
    }
}
