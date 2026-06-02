using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Field.Forms;

namespace Honua.Collect.Core.Ai;

/// <summary>Why an extracted field was not applied.</summary>
public enum AiSkipReason
{
    /// <summary>The field is not on the form.</summary>
    UnknownField,

    /// <summary>The model's confidence was below the threshold.</summary>
    LowConfidence,

    /// <summary>The field already has a value and overwriting was not requested.</summary>
    AlreadyFilled,
}

/// <summary>An extracted field that was not applied, with the reason.</summary>
/// <param name="FieldId">Field id.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record AiSkippedField(string FieldId, AiSkipReason Reason);

/// <summary>Options controlling how extracted fields are applied.</summary>
public sealed record AiApplyOptions
{
    /// <summary>Minimum confidence (0..1) required to apply a field. Defaults to 0.5.</summary>
    public double ConfidenceThreshold { get; init; } = 0.5;

    /// <summary>Whether to overwrite fields that already have a value. Defaults to <see langword="false"/>.</summary>
    public bool OverwriteExisting { get; init; }
}

/// <summary>The outcome of applying an extraction to a form.</summary>
/// <param name="Applied">Field ids that were written.</param>
/// <param name="Skipped">Fields that were not written, with reasons.</param>
public sealed record AiApplyOutcome(IReadOnlyList<string> Applied, IReadOnlyList<AiSkippedField> Skipped);

/// <summary>
/// Applies AI-extracted field values to a live form (BACKLOG A1/A2). This is the
/// Pro-gated capture differentiator: a voice or photo provider returns candidate
/// values and this writes the trustworthy ones into the form, leaving the user
/// to confirm. Low-confidence and already-filled fields are skipped so the model
/// assists rather than overwrites.
/// </summary>
public sealed class AiCaptureService
{
    private readonly CollectEntitlements _entitlements;

    /// <summary>Creates the service for an entitlement context.</summary>
    /// <param name="entitlements">Edition entitlements; AI capture requires Pro.</param>
    public AiCaptureService(CollectEntitlements entitlements)
        => _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));

    /// <summary>Whether the current edition unlocks AI-assisted capture.</summary>
    public bool IsAvailable => _entitlements.Allows(CollectFeature.AiAssistedCapture);

    /// <summary>
    /// Applies an extraction result to a form session under confidence gating.
    /// Throws <see cref="FeatureNotEntitledException"/> when the edition does not
    /// include AI-assisted capture.
    /// </summary>
    /// <param name="session">Form session to fill.</param>
    /// <param name="result">Extraction result from a provider.</param>
    /// <param name="options">Application options.</param>
    /// <returns>Which fields were applied and which were skipped.</returns>
    public AiApplyOutcome Apply(FormSession session, FieldExtractionResult result, AiApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        _entitlements.Require(CollectFeature.AiAssistedCapture);

        var opts = options ?? new AiApplyOptions();
        var fieldIds = session.Fields.Select(f => f.FieldId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = new List<string>();
        var skipped = new List<AiSkippedField>();

        foreach (var extracted in result.Fields)
        {
            if (!fieldIds.Contains(extracted.FieldId))
            {
                skipped.Add(new AiSkippedField(extracted.FieldId, AiSkipReason.UnknownField));
                continue;
            }

            if (extracted.Confidence < opts.ConfidenceThreshold)
            {
                skipped.Add(new AiSkippedField(extracted.FieldId, AiSkipReason.LowConfidence));
                continue;
            }

            if (!opts.OverwriteExisting && !IsMissing(session.GetValue(extracted.FieldId)))
            {
                skipped.Add(new AiSkippedField(extracted.FieldId, AiSkipReason.AlreadyFilled));
                continue;
            }

            session.SetValue(extracted.FieldId, extracted.Value);
            applied.Add(extracted.FieldId);
        }

        return new AiApplyOutcome(applied, skipped);
    }

    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        _ => false,
    };
}
