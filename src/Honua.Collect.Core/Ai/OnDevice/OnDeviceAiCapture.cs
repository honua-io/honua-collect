using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Ai.OnDevice;

/// <summary>
/// The result of transcribing an audio capture to text (BACKLOG A1 / Epic #42).
/// Platform-neutral: an on-device Whisper model, a cloud speech provider, or a
/// deterministic stub all return this shape, so the capture flow never depends on
/// which engine produced the words.
/// </summary>
/// <param name="Text">The transcribed text (may be empty when nothing was heard).</param>
/// <param name="Confidence">Overall transcription confidence in the range 0..1.</param>
/// <param name="Language">Optional BCP-47 language tag the model detected.</param>
public sealed record AiTranscription(string Text, double Confidence, string? Language = null)
{
    /// <summary>An empty transcription (no speech / no-op provider).</summary>
    public static AiTranscription Empty { get; } = new(string.Empty, 0.0);
}

/// <summary>
/// A single candidate field value produced by an on-device or cloud AI pass, with
/// the model's confidence and a short, human-readable rationale the review UI can
/// surface. This is the raw, <em>un-applied</em> suggestion — mapping it onto a form
/// (type coercion, validation, threshold gating, never-overwrite) is the job of
/// <see cref="AiSuggestionMapper"/>.
/// </summary>
/// <param name="FieldId">Target field id.</param>
/// <param name="Value">Suggested value, as the model produced it (typically text).</param>
/// <param name="Confidence">Model confidence in the range 0..1.</param>
/// <param name="Rationale">Optional short explanation (e.g. the matched phrase).</param>
public sealed record AiFieldSuggestion(string FieldId, object? Value, double Confidence, string? Rationale = null);

/// <summary>
/// The output of an extraction pass (voice, photo, or text → fields): a set of
/// candidate <see cref="AiFieldSuggestion"/>s plus any free text the model could
/// not map to a field. Provider-agnostic; <see cref="AiSuggestionMapper"/> turns
/// it into confirmable, type-coerced suggestions against a live form session.
/// </summary>
public sealed record AiFieldSuggestions
{
    /// <summary>The candidate field values.</summary>
    public IReadOnlyList<AiFieldSuggestion> Suggestions { get; init; } = [];

    /// <summary>Optional free text that mapped to no field (e.g. unrecognised speech).</summary>
    public string? Unmapped { get; init; }

    /// <summary>An empty suggestion set (no-op provider, nothing extracted).</summary>
    public static AiFieldSuggestions Empty { get; } = new();
}

/// <summary>
/// The platform-neutral provider seam for AI-assisted capture (Epics #42 + #6).
/// An implementation may be a future on-device Whisper/vision model running with no
/// signal, or a cloud provider (e.g. the Anthropic vision provider) used as a
/// higher-accuracy online fallback. The seam takes raw bytes — not host paths — so
/// it has no platform or filesystem dependency and can run in-process on any device.
/// </summary>
/// <remarks>
/// Implementations should be robust: extraction failures (bad audio, an unloadable
/// model, a network error in a cloud impl) must degrade to an empty result rather
/// than throw, so capture falls back to manual entry. Entitlement gating (Pro) is
/// enforced when suggestions are <em>applied</em> by <see cref="AiCaptureService"/>;
/// providers only produce candidates.
/// </remarks>
public interface IOnDeviceAiCapture
{
    /// <summary>A stable identifier for the engine (e.g. "stub", "whisper-tiny").</summary>
    string EngineId { get; }

    /// <summary>
    /// Whether this provider can run with no network connection. On-device models
    /// return <see langword="true"/>; a cloud fallback returns <see langword="false"/>.
    /// </summary>
    bool SupportsOffline { get; }

    /// <summary>Transcribes an audio capture to text (voice-to-fields, step 1).</summary>
    /// <param name="audio">Raw audio bytes (e.g. WAV/PCM from the mic).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transcription; <see cref="AiTranscription.Empty"/> on failure.</returns>
    Task<AiTranscription> TranscribeAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default);

    /// <summary>Extracts field suggestions from a photo (photo-to-fields, A2).</summary>
    /// <param name="photo">Raw image bytes.</param>
    /// <param name="form">Form whose fields bound the extraction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidate field values; <see cref="AiFieldSuggestions.Empty"/> on failure.</returns>
    Task<AiFieldSuggestions> ExtractFieldsFromPhotoAsync(
        ReadOnlyMemory<byte> photo,
        FormDefinition form,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts field suggestions from a transcript or any free text (voice-to-fields,
    /// A1, after transcription). This is deterministic and offline-capable in the
    /// rule-based default, so it is the core verifiable path today.
    /// </summary>
    /// <param name="text">Transcribed or typed free text.</param>
    /// <param name="form">Form whose fields bound the extraction.</param>
    /// <returns>Candidate field values; <see cref="AiFieldSuggestions.Empty"/> when nothing matched.</returns>
    AiFieldSuggestions ExtractFieldsFromText(string text, FormDefinition form);
}
