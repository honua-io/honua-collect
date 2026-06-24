using Honua.Collect.Core.Field;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms.Cascade;

/// <summary>
/// Declares that a choice field's available options cascade from (are filtered
/// by) the value of a parent field — the "cascading / dependent select" parity
/// item (BACKLOG F3). This goes <em>beyond</em> the existing
/// <see cref="FieldVisibilityRule"/>, which only shows or hides a field: here the
/// field stays visible but its <see cref="FormField.Choices"/> are narrowed to
/// the subset whose <see cref="FieldChoice.ParentValue"/> matches the parent's
/// current value.
/// </summary>
/// <remarks>
/// <para>
/// The SDK <see cref="FieldChoice"/> already models the cascade <em>data</em> via
/// <see cref="FieldChoice.ParentValue"/> (the parent value each option belongs
/// to). What the SDK <see cref="FormField"/> does not carry is which field is the
/// <em>parent</em>, so — exactly like repeat-row bounds — that linkage is supplied
/// to the runtime here rather than baked into the form schema.
/// </para>
/// <para>
/// Rules chain: a child can itself be the parent of a grandchild, giving
/// multi-level cascades (country → region → city). A choice with a null/empty
/// <see cref="FieldChoice.ParentValue"/> is treated as unparented and is always
/// available (so partially-tagged choice sets degrade gracefully).
/// </para>
/// </remarks>
/// <param name="FieldId">The choice field whose options are filtered.</param>
/// <param name="ParentFieldId">The field whose value selects the option subset.</param>
public sealed record ChoiceCascadeRule(string FieldId, string ParentFieldId)
{
    /// <summary>The choice field whose options are filtered.</summary>
    public string FieldId { get; } = !string.IsNullOrWhiteSpace(FieldId)
        ? FieldId
        : throw new ArgumentException("FieldId is required.", nameof(FieldId));

    /// <summary>The parent field whose value selects the available subset.</summary>
    public string ParentFieldId { get; } = !string.IsNullOrWhiteSpace(ParentFieldId)
        ? ParentFieldId
        : throw new ArgumentException("ParentFieldId is required.", nameof(ParentFieldId));
}

/// <summary>
/// Filters a choice field's options by its parent field's value, implementing the
/// cascading-select evaluation (BACKLOG F3). Stateless: given the field's full
/// option list, the cascade rule, and the current record values, it returns the
/// options that apply right now.
/// </summary>
public static class ChoiceFilter
{
    /// <summary>
    /// Returns the options available for <paramref name="field"/> given the current
    /// parent value. When no cascade rule applies, the field's full option list is
    /// returned unchanged.
    /// </summary>
    /// <param name="field">The choice field definition.</param>
    /// <param name="rule">The cascade rule for this field, or <see langword="null"/>.</param>
    /// <param name="values">Current record values (read for the parent value).</param>
    /// <returns>The filtered options, in their original order.</returns>
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

    /// <summary>
    /// Determines whether a currently-selected value is still valid given the
    /// available options. Used to clear a child select when its parent changes and
    /// the previously chosen option no longer belongs to the new parent.
    /// </summary>
    /// <param name="selected">The current field value.</param>
    /// <param name="available">The options now available.</param>
    /// <returns><see langword="true"/> when the value is empty or present in the options.</returns>
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
