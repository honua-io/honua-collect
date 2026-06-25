using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms.Localization;

/// <summary>
/// Resolves the active language for a multi-language form (BACKLOG F2) and hands
/// the matching <see cref="FormDefinition"/> to the runtime. A form is authored
/// in one <see cref="DefaultLanguage"/> and ships zero or more
/// <see cref="FormTranslations"/>; this service picks the active language and
/// localizes the form for it, falling back to the authored text for any label,
/// hint, or choice the active language does not translate.
/// </summary>
/// <remarks>
/// Resolution order for any one string is: the active language's translation, then
/// the authored (default-language) text. Choosing a language that has no
/// translation set — or the <see cref="DefaultLanguage"/> itself — yields the
/// authored form unchanged, so a respondent always sees complete text. The active
/// language can be seeded from <see cref="ILocaleStore"/> so the user's last choice
/// survives an app restart.
/// </remarks>
public sealed class FormLocalization
{
    private readonly Dictionary<string, FormTranslations> _translations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a localization service for a form authored in <paramref name="defaultLanguage"/>.</summary>
    /// <param name="defaultLanguage">BCP-47 code the form's authored text is written in (for example <c>en</c>).</param>
    /// <param name="translations">Per-language translation sets; the active language is chosen from these.</param>
    public FormLocalization(string defaultLanguage, params FormTranslations[] translations)
        : this(defaultLanguage, (IEnumerable<FormTranslations>)translations)
    {
    }

    /// <summary>Creates a localization service for a form authored in <paramref name="defaultLanguage"/>.</summary>
    /// <param name="defaultLanguage">BCP-47 code the form's authored text is written in.</param>
    /// <param name="translations">Per-language translation sets.</param>
    public FormLocalization(string defaultLanguage, IEnumerable<FormTranslations> translations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultLanguage);
        ArgumentNullException.ThrowIfNull(translations);

        DefaultLanguage = defaultLanguage;
        ActiveLanguage = defaultLanguage;

        foreach (var translation in translations)
        {
            ArgumentNullException.ThrowIfNull(translation);
            _translations[translation.Language] = translation;
        }
    }

    /// <summary>The language the form's authored text is written in; the fallback for every string.</summary>
    public string DefaultLanguage { get; }

    /// <summary>The currently selected language. Defaults to <see cref="DefaultLanguage"/>.</summary>
    public string ActiveLanguage { get; private set; }

    /// <summary>Languages this form can be presented in (the default plus every translation).</summary>
    public IReadOnlyCollection<string> AvailableLanguages
    {
        get
        {
            var languages = new List<string> { DefaultLanguage };
            languages.AddRange(_translations.Keys.Where(k => !LanguageEquals(k, DefaultLanguage)));
            return languages;
        }
    }

    /// <summary>Whether the given language can be selected (it is the default or has a translation set).</summary>
    /// <param name="language">BCP-47 language code.</param>
    /// <returns><see langword="true"/> if the language is available.</returns>
    public bool SupportsLanguage(string language)
        => !string.IsNullOrWhiteSpace(language)
            && (LanguageEquals(language, DefaultLanguage) || _translations.ContainsKey(language));

    /// <summary>
    /// Selects the active language. A supported language is applied; an unsupported
    /// one (no translation set and not the default) falls back to
    /// <see cref="DefaultLanguage"/> so the form never renders blank.
    /// </summary>
    /// <param name="language">BCP-47 language code to activate.</param>
    /// <returns><see langword="true"/> if the requested language was supported and applied.</returns>
    public bool SetActiveLanguage(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language) && SupportsLanguage(language))
        {
            ActiveLanguage = language;
            return true;
        }

        ActiveLanguage = DefaultLanguage;
        return false;
    }

    /// <summary>
    /// Returns the form localized to the <see cref="ActiveLanguage"/>. When the
    /// active language is the default (or has no translation set), the authored form
    /// is returned unchanged; otherwise every translated label, hint, and choice is
    /// swapped, and anything untranslated keeps its authored text.
    /// </summary>
    /// <param name="form">The authored form definition.</param>
    /// <returns>The localized form for the active language.</returns>
    public FormDefinition Localize(FormDefinition form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return _translations.TryGetValue(ActiveLanguage, out var translations)
            ? FormLocalizer.Localize(form, translations)
            : form;
    }

    /// <summary>Sets the active language and persists it so it survives an app restart (BACKLOG F2).</summary>
    /// <param name="store">The locale store to write through.</param>
    /// <param name="formId">Form whose active language is being set.</param>
    /// <param name="language">BCP-47 language code to activate and persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the requested language was supported and applied.</returns>
    public async Task<bool> SetAndPersistActiveLanguageAsync(
        ILocaleStore store,
        string formId,
        string language,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        var applied = SetActiveLanguage(language);
        await store.SetActiveLanguageAsync(formId, ActiveLanguage, ct).ConfigureAwait(false);
        return applied;
    }

    /// <summary>
    /// Seeds the active language from a persisted choice (BACKLOG F2). A stored
    /// language that is still supported is activated; an absent or no-longer-supported
    /// one leaves the active language at <see cref="DefaultLanguage"/>.
    /// </summary>
    /// <param name="store">The locale store to read from.</param>
    /// <param name="formId">Form whose active language to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved active language.</returns>
    public async Task<string> SeedActiveLanguageAsync(
        ILocaleStore store,
        string formId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        var stored = await store.GetActiveLanguageAsync(formId, ct).ConfigureAwait(false);
        SetActiveLanguage(stored);
        return ActiveLanguage;
    }

    private static bool LanguageEquals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
