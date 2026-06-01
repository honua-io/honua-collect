using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field;

/// <summary>
/// Product-owned media capture metadata that keeps local file paths outside the
/// SDK field contract. Ported from the former <c>Honua.Mobile.Field</c> package
/// (was <c>MobileFieldMediaAttachment</c>); renamed to avoid colliding with the
/// SDK's portable <see cref="FieldMediaAttachment"/>.
/// </summary>
public sealed record CapturedMediaAttachment
{
    /// <summary>Stable attachment identifier.</summary>
    public required string AttachmentId { get; init; }

    /// <summary>SDK field that owns this attachment, when known.</summary>
    public string? FieldId { get; init; }

    /// <summary>Local file-system path for the captured media.</summary>
    public required string LocalPath { get; init; }

    /// <summary>SDK media type.</summary>
    public FieldMediaType MediaType { get; init; }

    /// <summary>Media content type, when known.</summary>
    public string? ContentType { get; init; }

    /// <summary>Media size in bytes, when known.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Location where the media was captured.</summary>
    public FieldGeoPoint? CaptureLocation { get; init; }

    /// <summary>UTC time the media was captured.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Whether the host should blur faces before upload or export.</summary>
    public bool RequiresFaceBlur { get; init; }

    /// <summary>
    /// Product-owned evidence metadata, such as AR scene anchoring context. This is
    /// intentionally excluded from SDK attachment conversion until the SDK owns a
    /// portable evidence contract.
    /// </summary>
    public IReadOnlyDictionary<string, object?> EvidenceMetadata { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Returns a copy with merged evidence metadata.</summary>
    /// <param name="metadata">Metadata to merge into the attachment.</param>
    /// <returns>A copy containing the merged metadata.</returns>
    public CapturedMediaAttachment WithEvidenceMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var merged = new Dictionary<string, object?>(EvidenceMetadata, StringComparer.Ordinal);
        foreach (var item in metadata)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            merged[item.Key] = item.Value;
        }

        return this with { EvidenceMetadata = merged };
    }

    /// <summary>Converts capture metadata to the portable SDK attachment contract.</summary>
    /// <returns>SDK field media attachment without host-local file-system paths.</returns>
    public FieldMediaAttachment ToSdkAttachment()
    {
        return new FieldMediaAttachment
        {
            AttachmentId = AttachmentId,
            FieldId = FieldId,
            MediaType = MediaType,
            FileName = Path.GetFileName(LocalPath),
            ContentType = ContentType,
            SizeBytes = SizeBytes,
            CaptureLocation = CaptureLocation,
            CapturedAtUtc = CapturedAtUtc,
            RequiresFaceBlur = RequiresFaceBlur,
        };
    }
}
