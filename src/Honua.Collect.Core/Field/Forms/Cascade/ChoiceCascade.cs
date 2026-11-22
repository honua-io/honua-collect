// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms.Cascade;

public sealed record ChoiceCascadeRule(string FieldId, string ParentFieldId)
{
    public string FieldId { get; } = !string.IsNullOrWhiteSpace(FieldId)
        ? FieldId
        : throw new ArgumentException("FieldId is required.", nameof(FieldId));

    public string ParentFieldId { get; } = !string.IsNullOrWhiteSpace(ParentFieldId)
        ? ParentFieldId
        : throw new ArgumentException("ParentFieldId is required.", nameof(ParentFieldId));
}

public static class ChoiceFilter
{
    public static IReadOnlyList<FieldChoice> Available(
        FormField field,
        ChoiceCascadeRule? rule,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(values);

        var choices = field.Choices;
        if (rule is null || choices is null || choices.Count == 0)
        {
            return choices ?? [];
        }

        // No parent value selected yet: the dependent select has nothing to scope
        // to, so present an empty option list (the user must pick the parent
        // first). Unparented options (no ParentValue) remain universally available.
        values.TryGetValue(rule.ParentFieldId, out var parentValue);
        var parentText = FieldValues.ToText(parentValue);
        var hasParent = !string.IsNullOrEmpty(parentText);

        var filtered = new List<FieldChoice>(choices.Count);
        foreach (var choice in choices)
        {
            if (string.IsNullOrEmpty(choice.ParentValue))
            {
                // Unparented option — always offered.
                filtered.Add(choice);
                continue;
            }

            if (hasParent && FieldValues.AreEqual(choice.ParentValue, parentValue))
            {
                filtered.Add(choice);
            }
        }

        return filtered;
    }

    public static bool IsStillValid(object? selected, IReadOnlyList<FieldChoice> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        var text = FieldValues.ToText(selected);
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        foreach (var choice in available)
        {
            if (FieldValues.AreEqual(choice.Value, selected))
            {
                return true;
            }
        }

        return false;
    }
}
