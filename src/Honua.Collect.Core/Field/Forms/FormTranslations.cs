// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Forms;

public sealed record FormTranslations
{
    public required string Language { get; init; }

    public IReadOnlyDictionary<string, string> FieldLabels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> FieldHelp { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> SectionLabels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ChoiceLabels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static string ChoiceKey(string fieldId, string choiceValue) => $"{fieldId}/{choiceValue}";
}
