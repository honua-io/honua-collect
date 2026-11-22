// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Annotation;
using Honua.Collect.Core.Field.Capture;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Forms;

/// <summary>
/// View-model for one field on a capture screen. Binds a control to the live
/// <see cref="FieldState"/> in the runtime: editing <see cref="Value"/> pushes
/// through to the <see cref="ICaptureHost"/> (the form session or a repeat row),
/// which recomputes visibility, calculated fields, and validation; the parent
/// then calls <see cref="Refresh"/> so every field reflects the new state.
/// </summary>
public sealed class FieldViewModel : ObservableObject
{
    private readonly ICaptureHost _host;
    private readonly Action _onChanged;

    /// <summary>Creates a field view-model bound to a host field.</summary>
    /// <param name="host">The form session or repeat row that owns the field.</param>
    /// <param name="fieldId">Field identifier.</param>
    /// <param name="onChanged">Callback the setter invokes so the parent can refresh siblings.</param>
    public FieldViewModel(ICaptureHost host, string fieldId, Action onChanged)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        FieldId = fieldId;
        Widget = CaptureWidget.For(State.Field.Type);
    }

    private FieldState State => _host.GetField(FieldId);

    /// <summary>Stable field identifier.</summary>
    public string FieldId { get; }

    /// <summary>Field label.</summary>
    public string Label => State.Field.Label;

    /// <summary>Optional help text.</summary>
    public string? HelpText => State.Field.HelpText;

    /// <summary>Whether a value is required.</summary>
    public bool IsRequired => State.IsRequired;

    /// <summary>The widget kind a renderer should present.</summary>
    public CaptureWidgetKind Widget { get; }

    /// <summary>Label for the capture button, by widget kind.</summary>
    public string CaptureActionLabel => Widget switch
    {
        CaptureWidgetKind.Photo => "Add photo",
        CaptureWidgetKind.Video => "Record video",
        CaptureWidgetKind.Audio => "Record audio",
        CaptureWidgetKind.Signature => "Sign",
        CaptureWidgetKind.Sketch => "Sketch",
        CaptureWidgetKind.Barcode => "Scan barcode",
        CaptureWidgetKind.RecordLink => "Manage related records",
        _ => "Add attachment",
    };

    /// <summary>
    /// Allowed choices for choice/classification fields. For a cascading/dependent
    /// select (BACKLOG F3) this is the subset currently permitted by the parent
    /// field's value; for a plain choice field it is the field's full option list.
    /// </summary>
    public IReadOnlyList<FieldChoice> Choices => State.AvailableChoices;

    /// <summary>
    /// The selected choice for single-choice pickers. Maps between the stored
    /// value (a choice <see cref="FieldChoice.Value"/> string) and the
    /// <see cref="FieldChoice"/> a <c>Picker.SelectedItem</c> binds to, so the
    /// field stores the value — not the choice object.
    /// </summary>
    public FieldChoice? SelectedChoice
    {
        get
        {
            var current = State.Value?.ToString();
            return Choices.FirstOrDefault(c => string.Equals(c.Value, current, StringComparison.Ordinal));
        }
        set => Value = value?.Value;
    }

    /// <summary>Whether the field is currently shown.</summary>
    public bool IsVisible => State.IsVisible;

    /// <summary>Validation messages joined for display.</summary>
    public string ErrorText => string.Join(" ", State.Errors);

    /// <summary>Whether the field currently has a validation error.</summary>
    public bool HasError => State.Errors.Count > 0;

    /// <summary>The current value. Setting it pushes through the runtime.</summary>
    public object? Value
    {
        get => State.Value;
        set
        {
            if (!Equals(State.Value, value))
            {
                _host.SetValue(FieldId, value);
                _onChanged();
            }
        }
    }

    /// <summary>Number of media attachments captured for this field.</summary>
    public int MediaCount => State.Media.Count;

    /// <summary>Whether at least one media attachment has been captured for this field.</summary>
    public bool HasMedia => State.Media.Count > 0;

    /// <summary>Whether the field's media count policy permits capturing another item.</summary>
    public bool CanCaptureMore =>
        State.Field.Validation.MaxMediaCount is not { } max || MediaCount < max;

    /// <summary>Presentation summaries for each media attachment captured on this field.</summary>
    public IReadOnlyList<MediaAttachmentItemViewModel> MediaItems =>
        State.Media.Select(a => new MediaAttachmentItemViewModel(a)).ToList();

    /// <summary>Number of linked child records for a <see cref="CaptureWidgetKind.RecordLink"/> field.</summary>
    public int RelatedCount => State.Value switch
    {
        IReadOnlyCollection<FieldRecordLinkValue> links => links.Count,
        ICollection<FieldRecordLinkValue> links => links.Count,
        _ => 0,
    };

    /// <summary>
    /// Registers a captured media file against this field (used by the photo,
    /// video, audio, signature, and sketch widgets). For media fields, enforces
    /// <see cref="FieldMediaCapturePolicy"/> via <see cref="MediaCaptureField"/>,
    /// stamps <see cref="CapturedMediaAttachment.MediaType"/>, geotag location,
    /// rich metadata, and optional markup overlay, then refreshes siblings.
    /// </summary>
    /// <param name="localPath">Host-local path to the captured file.</param>
    /// <param name="contentType">Media content type, when known.</param>
    /// <param name="sizeBytes">Media file size in bytes, when known.</param>
    /// <param name="location">Capture GPS geotag, when available and permitted by policy.</param>
    /// <param name="metadata">Optional rich capture metadata (caption, duration, dimensions, geotag extras).</param>
    /// <param name="annotationOverlay">Optional structured markup overlay.</param>
    /// <returns>The captured attachment that was added.</returns>
    public CapturedMediaAttachment CaptureMedia(
        string localPath,
        string? contentType = null,
        long? sizeBytes = null,
        FieldGeoPoint? location = null,
        MediaCaptureMetadata? metadata = null,
        PhotoAnnotationOverlay? annotationOverlay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        CapturedMediaAttachment attachment;
        if (FormFieldTypes.IsMedia(State.Field.Type))
        {
            var widget = new MediaCaptureField(_host, State.Field);
            attachment = widget.Capture(
                localPath,
                contentType: contentType,
                sizeBytes: sizeBytes,
                location: location,
                metadata: metadata,
                annotationOverlay: annotationOverlay);
        }
        else
        {
            attachment = new CapturedMediaAttachment
            {
                AttachmentId = Guid.NewGuid().ToString("n"),
                FieldId = FieldId,
                LocalPath = localPath,
                ContentType = contentType,
                SizeBytes = sizeBytes,
                CaptureLocation = location,
            };

            if (metadata is not null)
            {
                attachment = metadata.ApplyTo(attachment);
            }

            if (annotationOverlay is { Count: > 0 })
            {
                attachment = annotationOverlay.ApplyTo(attachment);
            }

            _host.AddMedia(attachment);
        }

        _onChanged();
        return attachment;
    }

    /// <summary>Removes a captured media attachment from this field and refreshes.</summary>
    /// <param name="attachmentId">Attachment identifier.</param>
    /// <returns><see langword="true"/> if an attachment was removed.</returns>
    public bool RemoveMedia(string attachmentId)
    {
        var removed = _host.RemoveMedia(FieldId, attachmentId);
        if (removed)
        {
            _onChanged();
        }

        return removed;
    }

    /// <summary>
    /// Attaches or replaces the <see cref="PhotoAnnotationOverlay"/> on an
    /// existing photo or sketch attachment, optionally updating its flattened
    /// image path while preserving the original unannotated source path.
    /// </summary>
    /// <param name="attachmentId">Existing attachment identifier.</param>
    /// <param name="overlay">Markup overlay to store.</param>
    /// <param name="annotatedLocalPath">Optional flattened image path.</param>
    /// <param name="annotatedContentType">Optional content type for the flattened image.</param>
    /// <param name="annotatedSizeBytes">Optional size in bytes for the flattened image.</param>
    /// <returns>The updated attachment.</returns>
    public CapturedMediaAttachment AnnotateMedia(
        string attachmentId,
        PhotoAnnotationOverlay overlay,
        string? annotatedLocalPath = null,
        string? annotatedContentType = null,
        long? annotatedSizeBytes = null)
    {
        var widget = new MediaCaptureField(_host, State.Field);
        var updated = widget.Annotate(
            attachmentId,
            overlay,
            annotatedLocalPath,
            annotatedContentType,
            annotatedSizeBytes);
        _onChanged();
        return updated;
    }

    /// <summary>Updates the caption stored in an attachment's rich metadata and refreshes.</summary>
    /// <param name="attachmentId">Existing attachment identifier.</param>
    /// <param name="caption">New caption, or <see langword="null"/>/whitespace to clear.</param>
    /// <returns>The updated attachment.</returns>
    public CapturedMediaAttachment UpdateMediaCaption(string attachmentId, string? caption)
    {
        var widget = new MediaCaptureField(_host, State.Field);
        var existing = widget.Attachments.FirstOrDefault(a => string.Equals(a.AttachmentId, attachmentId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Field '{FieldId}' has no attachment '{attachmentId}'.");

        var currentMeta = MediaCaptureMetadata.From(existing) ?? new MediaCaptureMetadata();
        var updated = widget.UpdateMetadata(attachmentId, currentMeta with { Caption = caption });
        _onChanged();
        return updated;
    }

    /// <summary>Re-reads runtime state and raises change notification for all bound members.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(MediaCount));
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(CanCaptureMore));
        OnPropertyChanged(nameof(MediaItems));
        OnPropertyChanged(nameof(RelatedCount));
    }
}
