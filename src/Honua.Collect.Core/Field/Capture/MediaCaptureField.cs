using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Capture;

/// <summary>
/// View-model for a media-capture field — photo, video, audio, signature, or
/// sketch (BACKLOG C1–C5). It is the binding target for the camera/recorder/
/// signature/sketch widgets: it enforces the field's
/// <see cref="FieldMediaCapturePolicy"/> (attachment count, allowed content
/// types, max size) <em>before</em> an attachment is added, rather than only
/// catching violations at submit-time validation, and stamps capture metadata
/// (location, face-blur requirement) onto each attachment.
/// </summary>
public sealed class MediaCaptureField
{
    private readonly ICaptureHost _host;

    /// <summary>Binds a media-capture widget to a field on a capture host.</summary>
    /// <param name="host">The form session or repeat row that owns the field.</param>
    /// <param name="field">The media field definition.</param>
    public MediaCaptureField(ICaptureHost host, FormField field)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(field);

        if (!TryMediaType(field.Type, out var mediaType))
        {
            throw new ArgumentException($"Field '{field.FieldId}' is {field.Type}, not a media field.", nameof(field));
        }

        _host = host;
        Field = field;
        MediaType = mediaType;
    }

    /// <summary>The media field definition.</summary>
    public FormField Field { get; }

    /// <summary>The SDK media type captured by this widget.</summary>
    public FieldMediaType MediaType { get; }

    /// <summary>The capture policy in effect (defaults when the field defines none).</summary>
    public FieldMediaCapturePolicy Policy => Field.MediaPolicy ?? new FieldMediaCapturePolicy();

    /// <summary>Attachments already captured for this field.</summary>
    public IReadOnlyList<CapturedMediaAttachment> Attachments =>
        _host.GetField(Field.FieldId).Media;

    /// <summary>Number of attachments captured.</summary>
    public int Count => Attachments.Count;

    /// <summary>Whether the policy allows capturing another attachment.</summary>
    public bool CanCaptureMore =>
        Field.Validation.MaxMediaCount is not { } max || Count < max;

    /// <summary>
    /// Captures an attachment after enforcing the policy. Throws
    /// <see cref="InvalidOperationException"/> when the count limit is reached and
    /// <see cref="ArgumentException"/> when content type or size is disallowed.
    /// </summary>
    /// <param name="localPath">Host-local path to the captured media file.</param>
    /// <param name="contentType">Media content type, when known.</param>
    /// <param name="sizeBytes">Media size in bytes, when known.</param>
    /// <param name="location">Capture location, when available and allowed by policy.</param>
    /// <param name="attachmentId">Optional explicit attachment id; generated when omitted.</param>
    /// <param name="capturedAtUtc">Optional capture time.</param>
    /// <returns>The attachment that was added.</returns>
    public CapturedMediaAttachment Capture(
        string localPath,
        string? contentType = null,
        long? sizeBytes = null,
        FieldGeoPoint? location = null,
        string? attachmentId = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        if (!CanCaptureMore)
        {
            throw new InvalidOperationException(
                $"{Field.Label} allows at most {Field.Validation.MaxMediaCount} media item(s).");
        }

        if (Policy.AllowedContentTypes.Count > 0 && contentType is not null &&
            !Policy.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{Field.Label} does not allow content type '{contentType}'.", nameof(contentType));
        }

        if (Policy.MaxAttachmentBytes is { } maxBytes && sizeBytes is { } size && size > maxBytes)
        {
            throw new ArgumentException(
                $"{Field.Label} attachment exceeds the {maxBytes}-byte limit.", nameof(sizeBytes));
        }

        var attachment = new CapturedMediaAttachment
        {
            AttachmentId = attachmentId ?? Guid.NewGuid().ToString("n"),
            FieldId = Field.FieldId,
            LocalPath = localPath,
            MediaType = MediaType,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            CaptureLocation = Policy.CaptureLocation ? location : null,
            CapturedAtUtc = capturedAtUtc ?? DateTimeOffset.UtcNow,
            RequiresFaceBlur = Policy.RequiresFaceBlur,
        };

        _host.AddMedia(attachment);
        return attachment;
    }

    /// <summary>Removes a captured attachment.</summary>
    /// <param name="attachmentId">Attachment identifier.</param>
    /// <returns><see langword="true"/> if an attachment was removed.</returns>
    public bool Remove(string attachmentId) => _host.RemoveMedia(Field.FieldId, attachmentId);

    private static bool TryMediaType(FormFieldType fieldType, out FieldMediaType mediaType)
    {
        switch (fieldType)
        {
            case FormFieldType.Photo: mediaType = FieldMediaType.Photo; return true;
            case FormFieldType.Video: mediaType = FieldMediaType.Video; return true;
            case FormFieldType.Audio: mediaType = FieldMediaType.Audio; return true;
            case FormFieldType.Signature: mediaType = FieldMediaType.Signature; return true;
            case FormFieldType.Sketch: mediaType = FieldMediaType.Sketch; return true;
            case FormFieldType.File: mediaType = FieldMediaType.File; return true;
            default: mediaType = default; return false;
        }
    }
}
