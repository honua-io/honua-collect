// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms.Localization;

public sealed class FormLocalization
{
    private readonly Dictionary<string, FormTranslations> _translations =
        new(StringComparer.OrdinalIgnoreCase);

    public FormLocalization(string defaultLanguage, params FormTranslations[] translations)
        : this(defaultLanguage, (IEnumerable<FormTranslations>)translations)
    {
    }

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

    public string DefaultLanguage { get; }

    public string ActiveLanguage { get; private set; }

    public IReadOnlyCollection<string> AvailableLanguages
    {
        get
        {
            var languages = new List<string> { DefaultLanguage };
            languages.AddRange(_translations.Keys.Where(k => !LanguageEquals(k, DefaultLanguage)));
            return languages;
        }
    }

    public bool SupportsLanguage(string language)
        => !string.IsNullOrWhiteSpace(language)
            && (LanguageEquals(language, DefaultLanguage) || _translations.ContainsKey(language));

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

    public FormDefinition Localize(FormDefinition form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return _translations.TryGetValue(ActiveLanguage, out var translations)
            ? FormLocalizer.Localize(form, translations)
            : form;
    }

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
