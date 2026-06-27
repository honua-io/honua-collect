using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Ai.OnDevice;

/// <summary>
/// A deterministic, offline, rule-based extractor that maps free text (a voice
/// transcript or typed note) onto form fields with no model and no network. It is
/// the verifiable default behind <see cref="StubOnDeviceAiCapture"/> and a useful
/// floor on its own: a real on-device Whisper/vision model plugs into the same
/// <see cref="IOnDeviceAiCapture"/> seam and supersedes it when available.
/// </summary>
/// <remarks>
/// <para>The matcher is keyword/regex driven and type-aware:</para>
/// <list type="bullet">
///   <item>For each data-bearing field it looks for "<c>label[:=] value</c>" or
///     "<c>label value</c>" near the field's label (or field id), case-insensitive.</item>
///   <item>The captured value is shaped to the field type: numerics take the first
///     number after the label; yes/no maps spoken affirmations/negations; single-
///     and multiple-choice match the field's declared choices by value or label.</item>
///   <item>Confidence reflects match quality: a delimited "<c>label: value</c>" hit
///     scores higher than a loose adjacency hit; a choice matched verbatim scores
///     higher than one inferred. Confidences are intentionally conservative so the
///     mapper's threshold gating, not the extractor, decides what is trustworthy.</item>
/// </list>
/// <para>Media, signature, calculated, and record-link fields are never matched —
/// they are not values that can be read out of free text.</para>
/// </remarks>
public static class RuleBasedTextFieldExtractor
{
    private const double DelimitedConfidence = 0.85;
    private const double AdjacentConfidence = 0.6;
    private const double ChoiceVerbatimConfidence = 0.9;
    private const double ChoiceInferredConfidence = 0.65;

    /// <summary>Extracts field suggestions from free text against a form.</summary>
    /// <param name="text">Transcript or note text.</param>
    /// <param name="form">Form whose fields bound extraction.</param>
    /// <returns>Candidate field values; empty when nothing matched.</returns>
    public static AiFieldSuggestions Extract(string text, FormDefinition form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(text))
        {
            return AiFieldSuggestions.Empty;
        }

        var suggestions = new List<AiFieldSuggestion>();
        var matchedSomething = false;

        foreach (var field in DataBearingFields(form))
        {
            var suggestion = MatchField(text, field);
            if (suggestion is not null)
            {
                suggestions.Add(suggestion);
                matchedSomething = true;
            }
        }

        return new AiFieldSuggestions
        {
            Suggestions = suggestions,
            Unmapped = matchedSomething ? null : text.Trim(),
        };
    }

    private static IEnumerable<FormField> DataBearingFields(FormDefinition form)
    {
        static bool IsExtractable(FormFieldType type)
            => !FormFieldTypes.IsMedia(type) && type is not (
                FormFieldType.Calculated or FormFieldType.RecordLink or
                FormFieldType.Location or FormFieldType.GeoShape or FormFieldType.GeoTrace);

        return (form.Sections ?? [])
            .Where(s => s is { Repeatable: false })
            .SelectMany(s => s.Fields ?? [])
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.FieldId) && IsExtractable(f.Type));
    }

    private static AiFieldSuggestion? MatchField(string text, FormField field)
    {
        var labels = LabelCandidates(field);

        return field.Type switch
        {
            FormFieldType.Numeric => MatchNumeric(text, field, labels),
            FormFieldType.YesNo => MatchYesNo(text, field, labels),
            FormFieldType.SingleChoice or FormFieldType.MultipleChoice or FormFieldType.Classification
                => MatchChoice(text, field, labels),
            _ => MatchText(text, field, labels),
        };
    }

    private static IReadOnlyList<string> LabelCandidates(FormField field)
    {
        var set = new List<string>();
        if (!string.IsNullOrWhiteSpace(field.Label))
        {
            set.Add(field.Label.Trim());
        }

        // The field id is a fallback keyword; split camelCase / snake_case into words
        // (e.g. "treeSpecies" -> "tree species") so a spoken label still matches.
        var humanized = Humanize(field.FieldId);
        if (!string.IsNullOrWhiteSpace(humanized) &&
            !set.Any(s => string.Equals(s, humanized, StringComparison.OrdinalIgnoreCase)))
        {
            set.Add(humanized);
        }

        return set;
    }

    private static string Humanize(string fieldId)
    {
        var spaced = Regex.Replace(fieldId, "([a-z0-9])([A-Z])", "$1 $2");
        spaced = spaced.Replace('_', ' ').Replace('-', ' ');
        return Regex.Replace(spaced, "\\s+", " ").Trim();
    }

    private static AiFieldSuggestion? MatchText(string text, FormField field, IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            // "label: rest of sentence" — capture up to the next sentence break.
            var delimited = Regex.Match(
                text,
                $@"\b{Regex.Escape(label)}\s*[:=]\s*(?<v>[^.;,\n]+)",
                RegexOptions.IgnoreCase);
            if (delimited.Success)
            {
                var value = delimited.Groups["v"].Value.Trim();
                if (value.Length > 0)
                {
                    return new AiFieldSuggestion(field.FieldId, value, DelimitedConfidence, $"matched '{label}:'");
                }
            }

            // "label is X" / "label X" — capture a short following token run.
            var adjacent = Regex.Match(
                text,
                $@"\b{Regex.Escape(label)}\s+(?:is\s+|was\s+)?(?<v>[^.;,\n]+)",
                RegexOptions.IgnoreCase);
            if (adjacent.Success)
            {
                var value = adjacent.Groups["v"].Value.Trim();
                if (value.Length > 0)
                {
                    return new AiFieldSuggestion(field.FieldId, value, AdjacentConfidence, $"near '{label}'");
                }
            }
        }

        return null;
    }

    private static AiFieldSuggestion? MatchNumeric(string text, FormField field, IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            // First number that follows the label (optionally through "is"/":").
            var match = Regex.Match(
                text,
                $@"\b{Regex.Escape(label)}\b[^0-9-]*(?<v>-?\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase);
            if (match.Success &&
                double.TryParse(match.Groups["v"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                return new AiFieldSuggestion(field.FieldId, number, DelimitedConfidence, $"number near '{label}'");
            }
        }

        return null;
    }

    private static AiFieldSuggestion? MatchYesNo(string text, FormField field, IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(
                text,
                $@"\b{Regex.Escape(label)}\b[^.;,\n]*?\b(?<v>yes|no|true|false|y|n)\b",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var token = match.Groups["v"].Value.ToLowerInvariant();
                var flag = token is "yes" or "true" or "y";
                return new AiFieldSuggestion(field.FieldId, flag, AdjacentConfidence, $"'{token}' near '{label}'");
            }
        }

        return null;
    }

    private static AiFieldSuggestion? MatchChoice(string text, FormField field, IReadOnlyList<string> labels)
    {
        var choices = (field.Choices ?? [])
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Value))
            .ToList();
        if (choices.Count == 0)
        {
            // No declared options — fall back to free-text capture.
            return MatchText(text, field, labels);
        }

        // Prefer a choice mentioned right after the label; otherwise any choice
        // mentioned anywhere in the text (lower confidence).
        foreach (var label in labels)
        {
            var labelHit = Regex.Match(text, $@"\b{Regex.Escape(label)}\b", RegexOptions.IgnoreCase);
            if (!labelHit.Success)
            {
                continue;
            }

            var tail = text[labelHit.Index..];
            foreach (var choice in choices)
            {
                if (MentionsChoice(tail, choice))
                {
                    return new AiFieldSuggestion(field.FieldId, choice.Value, ChoiceVerbatimConfidence, $"choice near '{label}'");
                }
            }
        }

        foreach (var choice in choices)
        {
            if (MentionsChoice(text, choice))
            {
                return new AiFieldSuggestion(field.FieldId, choice.Value, ChoiceInferredConfidence, "choice mentioned");
            }
        }

        return null;
    }

    private static bool MentionsChoice(string text, FieldChoice choice)
    {
        bool Mentions(string? token) =>
            !string.IsNullOrWhiteSpace(token) &&
            Regex.IsMatch(text, $@"\b{Regex.Escape(token!.Trim())}\b", RegexOptions.IgnoreCase);

        return Mentions(choice.Value) || Mentions(choice.Label);
    }
}
