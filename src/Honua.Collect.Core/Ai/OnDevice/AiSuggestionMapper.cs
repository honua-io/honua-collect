using System.Globalization;
using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Ai.OnDevice;

/// <summary>Why a mapped suggestion is not, or cannot be, applied to a form.</summary>
public enum AiSuggestionStatus
{
    /// <summary>Mapped, coerced, validated, and above threshold — safe to apply on confirm.</summary>
    Ready,

    /// <summary>The suggested field is not present on the form session.</summary>
    UnknownField,

    /// <summary>Confidence was below the configured threshold — flagged, not applied.</summary>
    LowConfidence,

    /// <summary>The value could not be coerced to the field's type, or failed type validation.</summary>
    InvalidValue,

    /// <summary>The field already holds a user-entered value — never silently overwritten.</summary>
    AlreadyFilled,
}

/// <summary>
/// A single AI suggestion after mapping onto a form session: the type-coerced value,
/// the model's confidence, a status saying whether it may be applied, and a flag for
/// whether the user has confirmed it. This is the human-in-the-loop unit — the UI
/// shows it, the user accepts or rejects it, and only accepted, <see cref="AiSuggestionStatus.Ready"/>
/// suggestions are written.
/// </summary>
public sealed record AiMappedSuggestion
{
    /// <summary>Target field id (canonical casing from the form).</summary>
    public required string FieldId { get; init; }

    /// <summary>The field's type, for the review UI.</summary>
    public required FormFieldType FieldType { get; init; }

    /// <summary>The value as the model proposed it (pre-coercion), for display.</summary>
    public object? RawValue { get; init; }

    /// <summary>The coerced value that would be written, or <see langword="null"/> when invalid.</summary>
    public object? CoercedValue { get; init; }

    /// <summary>Model confidence in the range 0..1.</summary>
    public required double Confidence { get; init; }

    /// <summary>The mapping status — whether and why the suggestion is applyable.</summary>
    public required AiSuggestionStatus Status { get; init; }

    /// <summary>Optional short rationale from the extractor.</summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// Whether the user has confirmed this suggestion. Defaults to <see langword="true"/>
    /// only for <see cref="AiSuggestionStatus.Ready"/> suggestions so a "accept all"
    /// flow applies the safe ones; the UI can toggle this per suggestion.
    /// </summary>
    public bool Accepted { get; init; }

    /// <summary>Whether this suggestion is currently applyable (ready and accepted).</summary>
    public bool IsApplyable => Status == AiSuggestionStatus.Ready && Accepted;
}

/// <summary>The outcome of mapping a suggestion set onto a form session.</summary>
/// <param name="Suggestions">Every suggestion with its status and coerced value.</param>
public sealed record AiSuggestionSet(IReadOnlyList<AiMappedSuggestion> Suggestions)
{
    /// <summary>Suggestions that are ready to apply (above threshold, valid, not overwriting).</summary>
    public IEnumerable<AiMappedSuggestion> Ready => Suggestions.Where(s => s.Status == AiSuggestionStatus.Ready);

    /// <summary>Suggestions flagged for the user to review (everything not ready).</summary>
    public IEnumerable<AiMappedSuggestion> Flagged => Suggestions.Where(s => s.Status != AiSuggestionStatus.Ready);
}

/// <summary>How many fields an apply pass wrote, and which suggestions it wrote.</summary>
/// <param name="AppliedFieldIds">Field ids written to the session.</param>
public sealed record AiApplyResult(IReadOnlyList<string> AppliedFieldIds);

/// <summary>
/// The platform-neutral core that makes AI capture <em>safe</em>: it maps raw
/// <see cref="AiFieldSuggestions"/> onto a live <see cref="FormSession"/> with
/// per-field confidence, type coercion and validation, confidence-threshold gating,
/// and a never-overwrite-a-user-value rule — producing confirmable
/// <see cref="AiMappedSuggestion"/>s. Nothing is written until the caller applies an
/// accepted, ready suggestion. No model, no network, fully deterministic and testable.
/// </summary>
public static class AiSuggestionMapper
{
    /// <summary>
    /// Maps a suggestion set onto a session, computing each suggestion's status,
    /// coerced value, and default acceptance — without mutating the session.
    /// </summary>
    /// <param name="session">The live form session.</param>
    /// <param name="suggestions">Raw suggestions from a provider.</param>
    /// <param name="options">Apply options (threshold, overwrite). Defaults when null.</param>
    /// <returns>The mapped, confirmable suggestion set.</returns>
    public static AiSuggestionSet Map(
        FormSession session,
        AiFieldSuggestions suggestions,
        AiApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(suggestions);

        var opts = options ?? new AiApplyOptions();
        var fields = session.Fields.ToDictionary(f => f.FieldId, StringComparer.OrdinalIgnoreCase);
        var mapped = new List<AiMappedSuggestion>(suggestions.Suggestions.Count);

        foreach (var suggestion in suggestions.Suggestions)
        {
            mapped.Add(MapOne(session, fields, suggestion, opts));
        }

        return new AiSuggestionSet(mapped);
    }

    /// <summary>
    /// Applies every applyable suggestion (ready and accepted) in a mapped set to the
    /// session. Suggestions that are flagged or unaccepted are never written, so the
    /// user's confirmation is authoritative and existing values are preserved.
    /// </summary>
    /// <param name="session">The session to write to.</param>
    /// <param name="set">A mapped set, typically after the user toggled acceptance.</param>
    /// <returns>The field ids that were written.</returns>
    public static AiApplyResult Apply(FormSession session, AiSuggestionSet set)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(set);

        var applied = new List<string>();
        var fieldIds = session.Fields.Select(f => f.FieldId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var suggestion in set.Suggestions.Where(s => s.IsApplyable))
        {
            // Re-check presence defensively; never write an unknown field.
            if (fieldIds.Contains(suggestion.FieldId))
            {
                session.SetValue(suggestion.FieldId, suggestion.CoercedValue);
                applied.Add(suggestion.FieldId);
            }
        }

        return new AiApplyResult(applied);
    }

    private static AiMappedSuggestion MapOne(
        FormSession session,
        IReadOnlyDictionary<string, FieldState> fields,
        AiFieldSuggestion suggestion,
        AiApplyOptions opts)
    {
        if (!fields.TryGetValue(suggestion.FieldId, out var state))
        {
            return new AiMappedSuggestion
            {
                FieldId = suggestion.FieldId,
                FieldType = FormFieldType.Text,
                RawValue = suggestion.Value,
                Confidence = Clamp(suggestion.Confidence),
                Status = AiSuggestionStatus.UnknownField,
                Rationale = suggestion.Rationale,
                Accepted = false,
            };
        }

        var fieldId = state.FieldId; // canonical casing
        var type = state.Field.Type;
        var confidence = Clamp(suggestion.Confidence);

        AiMappedSuggestion With(AiSuggestionStatus status, object? coerced)
            => new()
            {
                FieldId = fieldId,
                FieldType = type,
                RawValue = suggestion.Value,
                CoercedValue = coerced,
                Confidence = confidence,
                Status = status,
                Rationale = suggestion.Rationale,
                Accepted = status == AiSuggestionStatus.Ready,
            };

        // 1. Confidence gate first — a low-confidence value is flagged regardless of type.
        if (confidence < opts.ConfidenceThreshold)
        {
            return With(AiSuggestionStatus.LowConfidence, coerced: null);
        }

        // 2. Type coercion + validation. A value that cannot be made into the field's
        //    type, or that violates the field's declared validation, is rejected.
        if (!TryCoerce(suggestion.Value, state.Field, out var coerced))
        {
            return With(AiSuggestionStatus.InvalidValue, coerced: null);
        }

        // 3. Never overwrite a user-entered value unless explicitly asked.
        if (!opts.OverwriteExisting && !IsMissing(session.GetValue(fieldId)))
        {
            return With(AiSuggestionStatus.AlreadyFilled, coerced);
        }

        return With(AiSuggestionStatus.Ready, coerced);
    }

    /// <summary>
    /// Coerces a loosely-typed suggested value to the field's type and applies the
    /// field's declared range/length validation. Returns <see langword="false"/> when
    /// the value cannot be represented as the field type or fails validation.
    /// </summary>
    private static bool TryCoerce(object? value, FormField field, out object? coerced)
    {
        coerced = null;
        var text = FieldValues.ToText(value);

        switch (field.Type)
        {
            case FormFieldType.Numeric:
            {
                if (!FieldValues.TryAsDouble(value, out var number))
                {
                    return false;
                }

                if (!PassesNumericRange(number, field.Validation))
                {
                    return false;
                }

                // Preserve integers as long for clean storage; keep decimals as double.
                coerced = number == Math.Floor(number) && !double.IsInfinity(number)
                    ? (object)(long)number
                    : number;
                return true;
            }

            case FormFieldType.YesNo:
            {
                switch (value)
                {
                    case bool b:
                        coerced = b;
                        return true;
                }

                var token = text.Trim().ToLowerInvariant();
                switch (token)
                {
                    case "true" or "yes" or "y" or "1":
                        coerced = true;
                        return true;
                    case "false" or "no" or "n" or "0":
                        coerced = false;
                        return true;
                    default:
                        return false;
                }
            }

            case FormFieldType.SingleChoice:
            case FormFieldType.MultipleChoice:
            case FormFieldType.Classification:
            {
                var choices = field.Choices ?? [];
                if (choices.Count == 0)
                {
                    // Open choice field — accept as text under length validation.
                    return CoerceText(text, field, out coerced);
                }

                var match = choices.FirstOrDefault(c =>
                    c is not null &&
                    (string.Equals(c.Value, text, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.Label, text, StringComparison.OrdinalIgnoreCase)));
                if (match is null)
                {
                    return false; // not a permitted option
                }

                coerced = match.Value;
                return true;
            }

            case FormFieldType.Date:
            case FormFieldType.DateTime:
            {
                if (value is DateTimeOffset || value is DateTime)
                {
                    coerced = value;
                    return true;
                }

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                {
                    coerced = dto;
                    return true;
                }

                return false;
            }

            default:
                return CoerceText(text, field, out coerced);
        }
    }

    private static bool CoerceText(string text, FormField field, out object? coerced)
    {
        coerced = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var rule = field.Validation;
        if (rule is not null)
        {
            if (rule.MinLength is { } min && text.Length < min)
            {
                return false;
            }

            if (rule.MaxLength is { } max && text.Length > max)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rule.RegexPattern) &&
                !System.Text.RegularExpressions.Regex.IsMatch(text, rule.RegexPattern))
            {
                return false;
            }
        }

        coerced = text;
        return true;
    }

    private static bool PassesNumericRange(double number, FieldValidationRule? rule)
    {
        if (rule is null)
        {
            return true;
        }

        if (rule.MinNumericValue is { } min && number < min)
        {
            return false;
        }

        if (rule.MaxNumericValue is { } max && number > max)
        {
            return false;
        }

        return true;
    }

    private static double Clamp(double value) => double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);

    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        _ => false,
    };
}
