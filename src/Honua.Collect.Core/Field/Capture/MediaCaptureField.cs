// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Capture;

public sealed class MediaCaptureField
{
    private readonly ICaptureHost _host;

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

    public FormField Field { get; }

    public FieldMediaType MediaType { get; }

    public FieldMediaCapturePolicy Policy => Field.MediaPolicy ?? new FieldMediaCapturePolicy();

    public IReadOnlyList<CapturedMediaAttachment> Attachments =>
        _host.GetField(Field.FieldId).Media;

    public int Count => Attachments.Count;

    public bool CanCaptureMore =>
        Field.Validation.MaxMediaCount is not { } max || Count < max;

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
