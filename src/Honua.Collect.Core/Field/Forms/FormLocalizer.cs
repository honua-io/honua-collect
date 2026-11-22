// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms;

public static class FormLocalizer
{
    public static FormDefinition Localize(FormDefinition form, FormTranslations translations)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(translations);

        var sections = form.Sections.Select(section => LocalizeSection(section, translations)).ToList();
        return form with { Sections = sections };
    }

    private static FormSection LocalizeSection(FormSection section, FormTranslations translations)
    {
        var fields = section.Fields.Select(field => LocalizeField(field, translations)).ToList();

        return section with
        {
            Label = Lookup(translations.SectionLabels, section.SectionId, section.Label),
            Fields = fields,
        };
    }

    private static FormField LocalizeField(FormField field, FormTranslations translations)
    {
        var choices = field.Choices.Count == 0
            ? field.Choices
            : field.Choices.Select(choice => LocalizeChoice(field.FieldId, choice, translations)).ToList();

        return field with
        {
            Label = Lookup(translations.FieldLabels, field.FieldId, field.Label),
            HelpText = field.HelpText is null
                ? null
                : Lookup(translations.FieldHelp, field.FieldId, field.HelpText),
            Choices = choices,
        };
    }

    private static FieldChoice LocalizeChoice(string fieldId, FieldChoice choice, FormTranslations translations)
    {
        var children = choice.Children.Count == 0
            ? choice.Children
            : choice.Children.Select(child => LocalizeChoice(fieldId, child, translations)).ToList();

        var key = FormTranslations.ChoiceKey(fieldId, choice.Value);
        return choice with
        {
            Label = Lookup(translations.ChoiceLabels, key, choice.Label ?? choice.Value),
            Children = children,
        };
    }

    private static string Lookup(IReadOnlyDictionary<string, string> table, string key, string fallback)
        => table.TryGetValue(key, out var translated) && !string.IsNullOrWhiteSpace(translated)
            ? translated
            : fallback;
}
