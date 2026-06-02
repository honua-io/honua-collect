namespace Honua.Collect.Core.Field.Forms;

/// <summary>
/// A single language's translations for a form (BACKLOG F2 — multi-language /
/// localized forms). Survey123 sources these from XLSForm <c>label::lang</c>
/// columns; here they are a structured set the app loads alongside the form and
/// hands to <see cref="FormLocalizer"/>. Any text without a translation falls
/// back to the form's authored text.
/// </summary>
public sealed record FormTranslations
{
    /// <summary>BCP-47 language code these translations are for (e.g. <c>es</c>, <c>fr-CA</c>).</summary>
    public required string Language { get; init; }

    /// <summary>Translated field labels, keyed by field id.</summary>
    public IReadOnlyDictionary<string, string> FieldLabels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Translated field help text, keyed by field id.</summary>
    public IReadOnlyDictionary<string, string> FieldHelp { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Translated section labels, keyed by section id.</summary>
    public IReadOnlyDictionary<string, string> SectionLabels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Translated choice labels, keyed by <c>fieldId/choiceValue</c> (use
    /// <see cref="ChoiceKey"/> to build the key).
    /// </summary>
    public IReadOnlyDictionary<string, string> ChoiceLabels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the <see cref="ChoiceLabels"/> key for a field's choice value.</summary>
    /// <param name="fieldId">Owning field id.</param>
    /// <param name="choiceValue">Stored choice value.</param>
    /// <returns>The composite key.</returns>
    public static string ChoiceKey(string fieldId, string choiceValue) => $"{fieldId}/{choiceValue}";
}
