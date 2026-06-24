using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Field.Forms;

namespace Honua.Collect.Core.Vision;

/// <summary>How a label maps to a target field value.</summary>
public enum DetectionFieldKind
{
    /// <summary>
    /// Write the detection's label as the field value (a category/type field).
    /// When several labels are detected, the highest-confidence one wins.
    /// </summary>
    Category,

    /// <summary>
    /// Write the <em>count</em> of detections matching <see cref="DetectionFieldRule.Label"/>
    /// (or all detections when no label is set) as a numeric field value.
    /// </summary>
    Count,
}

/// <summary>
/// One rule binding detected objects onto a form field (epic #43). A
/// <see cref="DetectionFieldKind.Category"/> rule maps a detection's label to a
/// type/condition field; a <see cref="DetectionFieldKind.Count"/> rule maps the
/// number of matching detections to a numeric field (count poles/signs/defects).
/// </summary>
/// <param name="FieldId">Target form field id.</param>
/// <param name="Kind">Whether to write a category label or a detection count.</param>
public sealed record DetectionFieldRule(string FieldId, DetectionFieldKind Kind)
{
    /// <summary>
    /// For <see cref="DetectionFieldKind.Count"/>, only count detections with this
    /// label; for <see cref="DetectionFieldKind.Category"/>, only consider this label.
    /// Null counts/considers every detected label.
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>Why a detection mapping was not applied to a field.</summary>
public enum DetectionSkipReason
{
    /// <summary>The target field is not on the form.</summary>
    UnknownField,

    /// <summary>No detection cleared the confidence threshold for this rule.</summary>
    BelowThreshold,

    /// <summary>The field already has a user value and overwriting was not requested.</summary>
    AlreadyFilled,

    /// <summary>No detection matched the rule's label.</summary>
    NoMatch,
}

/// <summary>A field a mapping rule did not write, with the reason.</summary>
/// <param name="FieldId">Target field id.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record DetectionSkippedField(string FieldId, DetectionSkipReason Reason);

/// <summary>Options controlling how detections are mapped onto form fields.</summary>
public sealed record DetectionMappingOptions
{
    /// <summary>Minimum detection confidence (0..1) to consider. Defaults to 0.5.</summary>
    public double ConfidenceThreshold { get; init; } = 0.5;

    /// <summary>Whether to overwrite fields that already hold a value. Defaults to false.</summary>
    public bool OverwriteExisting { get; init; }
}

/// <summary>The outcome of mapping detections onto a form.</summary>
/// <param name="Applied">Field ids that were written.</param>
/// <param name="Skipped">Fields not written, with reasons.</param>
public sealed record DetectionMappingOutcome(
    IReadOnlyList<string> Applied,
    IReadOnlyList<DetectionSkippedField> Skipped);

/// <summary>
/// Maps spatial-CV detections onto a live form (epic #43): label→category field,
/// count→numeric field, under confidence-threshold gating and the same
/// never-overwrite-a-user-value safety as <c>AiCaptureService</c>. The model
/// assists; the operator confirms. Pro-gated like the rest of AI-assisted capture.
/// </summary>
public sealed class DetectionFieldMapper
{
    private readonly IEntitlements _entitlements;

    /// <summary>Creates the mapper for an entitlement context.</summary>
    /// <param name="entitlements">Edition entitlements; spatial CV requires Pro AI-assisted capture.</param>
    public DetectionFieldMapper(IEntitlements entitlements)
        => _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));

    /// <summary>Whether the current edition unlocks AI-assisted (spatial-CV) capture.</summary>
    public bool IsAvailable => _entitlements.Allows(CollectFeature.AiAssistedCapture);

    /// <summary>
    /// Applies detection-to-field rules to a form session under confidence and
    /// overwrite gating. Throws <see cref="FeatureNotEntitledException"/> when the
    /// edition does not include AI-assisted capture.
    /// </summary>
    /// <param name="session">Form session to fill.</param>
    /// <param name="result">Detection result from a detector.</param>
    /// <param name="rules">Field mapping rules.</param>
    /// <param name="options">Mapping options.</param>
    /// <returns>Which fields were applied and which were skipped.</returns>
    public DetectionMappingOutcome Apply(
        FormSession session,
        DetectionResult result,
        IReadOnlyList<DetectionFieldRule> rules,
        DetectionMappingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rules);
        _entitlements.Require(CollectFeature.AiAssistedCapture);

        var opts = options ?? new DetectionMappingOptions();
        var fieldIds = session.Fields.Select(f => f.FieldId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only confidence-cleared detections feed the mapping.
        var confident = result.Detections
            .Where(d => d.Confidence >= opts.ConfidenceThreshold)
            .ToList();

        var applied = new List<string>();
        var skipped = new List<DetectionSkippedField>();

        foreach (var rule in rules)
        {
            if (!fieldIds.Contains(rule.FieldId))
            {
                skipped.Add(new DetectionSkippedField(rule.FieldId, DetectionSkipReason.UnknownField));
                continue;
            }

            var matched = rule.Label is null
                ? confident
                : confident.Where(d => string.Equals(d.Label, rule.Label, StringComparison.OrdinalIgnoreCase)).ToList();

            object? value;
            switch (rule.Kind)
            {
                case DetectionFieldKind.Count:
                    // A count is meaningful even at zero, but if nothing cleared the
                    // threshold at all we treat it as "no signal" rather than asserting 0.
                    if (confident.Count == 0)
                    {
                        skipped.Add(new DetectionSkippedField(rule.FieldId, DetectionSkipReason.BelowThreshold));
                        continue;
                    }

                    value = matched.Count;
                    break;

                case DetectionFieldKind.Category:
                    var best = matched
                        .OrderByDescending(d => d.Confidence)
                        .FirstOrDefault();
                    if (best is null)
                    {
                        skipped.Add(new DetectionSkippedField(
                            rule.FieldId,
                            confident.Count == 0 ? DetectionSkipReason.BelowThreshold : DetectionSkipReason.NoMatch));
                        continue;
                    }

                    value = best.Label;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(rules), rule.Kind, "Unknown detection field kind.");
            }

            if (!opts.OverwriteExisting && !IsMissing(session.GetValue(rule.FieldId)))
            {
                skipped.Add(new DetectionSkippedField(rule.FieldId, DetectionSkipReason.AlreadyFilled));
                continue;
            }

            session.SetValue(rule.FieldId, value);
            applied.Add(rule.FieldId);
        }

        return new DetectionMappingOutcome(applied, skipped);
    }

    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        _ => false,
    };
}
