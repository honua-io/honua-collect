using Honua.Collect.Core.Field;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Ai;

/// <summary>
/// A single field value extracted by an AI provider, with the model's confidence.
/// </summary>
/// <param name="FieldId">Target field id.</param>
/// <param name="Value">Extracted value.</param>
/// <param name="Confidence">Model confidence in the range 0..1.</param>
public sealed record ExtractedField(string FieldId, object? Value, double Confidence);

/// <summary>
/// The result of an AI extraction pass over voice or a photo (BACKLOG A1/A2):
/// a set of candidate field values the user can accept. Providers return this;
/// <see cref="AiCaptureService"/> applies it to a form under confidence and
/// entitlement gating.
/// </summary>
public sealed record FieldExtractionResult
{
    /// <summary>Extracted candidate field values.</summary>
    public IReadOnlyList<ExtractedField> Fields { get; init; } = [];

    /// <summary>Optional free-text the model could not map to a field.</summary>
    public string? Unmapped { get; init; }
}

/// <summary>
/// Extracts field values from a spoken description (voice-to-fields, A1).
/// Implementations call an on-device or cloud speech+extraction model; the
/// contract lives here so the capture flow is provider-agnostic and testable.
/// </summary>
public interface IVoiceToFieldsProvider
{
    /// <summary>Extracts field values from an audio capture.</summary>
    /// <param name="audioPath">Host-local path to the audio file.</param>
    /// <param name="form">Form whose fields bound the extraction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extracted candidate values.</returns>
    Task<FieldExtractionResult> ExtractAsync(string audioPath, FormDefinition form, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extracts field values from a photo (photo-to-fields, A2) — object/attribute
/// recognition mapped onto form fields.
/// </summary>
public interface IPhotoToFieldsProvider
{
    /// <summary>Extracts field values from a photo.</summary>
    /// <param name="photoPath">Host-local path to the image.</param>
    /// <param name="form">Form whose fields bound the extraction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extracted candidate values.</returns>
    Task<FieldExtractionResult> ExtractAsync(string photoPath, FormDefinition form, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of redacting (e.g. face-blurring) a media attachment (A3).
/// </summary>
/// <param name="OutputPath">Host-local path to the redacted media.</param>
/// <param name="RegionsRedacted">Number of regions (faces, plates) blurred.</param>
public sealed record RedactionResult(string OutputPath, int RegionsRedacted);

/// <summary>
/// Executes media redaction such as face-blurring (A3). The form already marks
/// which attachments require it via <see cref="FieldMediaAttachment.RequiresFaceBlur"/>;
/// this performs the blur before upload or export.
/// </summary>
public interface IMediaRedactionProvider
{
    /// <summary>Redacts an attachment, returning the redacted output.</summary>
    /// <param name="attachment">Attachment requiring redaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The redaction result.</returns>
    Task<RedactionResult> RedactAsync(CapturedMediaAttachment attachment, CancellationToken cancellationToken = default);
}
