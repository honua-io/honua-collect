using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Ai.OnDevice;

/// <summary>
/// A deterministic, offline, model-free <see cref="IOnDeviceAiCapture"/> used as the
/// default provider until a real on-device Whisper/vision model is bound (Epic #42).
/// It is a genuine seam implementation, not a throwaway: text extraction is the
/// fully-working <see cref="RuleBasedTextFieldExtractor"/>, while audio and photo —
/// which need an actual model — return empty results rather than fabricating data,
/// so capture degrades safely to manual entry. Swapping in a Whisper/vision provider
/// is a one-line registration change.
/// </summary>
public sealed class StubOnDeviceAiCapture : IOnDeviceAiCapture
{
    /// <summary>A shared instance — the stub is stateless.</summary>
    public static StubOnDeviceAiCapture Instance { get; } = new();

    /// <inheritdoc />
    public string EngineId => "stub";

    /// <inheritdoc />
    public bool SupportsOffline => true;

    /// <inheritdoc />
    public Task<AiTranscription> TranscribeAudioAsync(
        ReadOnlyMemory<byte> audio,
        CancellationToken cancellationToken = default)
    {
        // No speech model here: return empty rather than invent words. A real
        // on-device Whisper provider replaces this.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AiTranscription.Empty);
    }

    /// <inheritdoc />
    public Task<AiFieldSuggestions> ExtractFieldsFromPhotoAsync(
        ReadOnlyMemory<byte> photo,
        FormDefinition form,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        // No vision model here: return empty rather than guess from pixels. A real
        // on-device vision provider (or the cloud Anthropic provider) replaces this.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AiFieldSuggestions.Empty);
    }

    /// <inheritdoc />
    public AiFieldSuggestions ExtractFieldsFromText(string text, FormDefinition form)
        => RuleBasedTextFieldExtractor.Extract(text, form);
}
