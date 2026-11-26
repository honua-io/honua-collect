// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Honua.Field.Core.Field;
using Honua.Field.Core.Field.Annotation;
using Honua.Field.Core.Field.Capture;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Presentation.Forms;

/// <summary>
/// Read-only presentation summary of a single <see cref="CapturedMediaAttachment"/>
/// bound to a media field, surfacing geotag, markup count, duration, dimensions,
/// and caption for the form UI without any MAUI dependency.
/// </summary>
public sealed class MediaAttachmentItemViewModel
{
    /// <summary>Creates a view-model item from a captured attachment.</summary>
    /// <param name="attachment">The captured media attachment.</param>
    public MediaAttachmentItemViewModel(CapturedMediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        Attachment = attachment;
        Metadata = MediaCaptureMetadata.From(attachment);
        AnnotationOverlay = PhotoAnnotationOverlay.From(attachment);

        GeotagSummary = MediaCaptureMetadata.FormatGeotag(attachment.CaptureLocation, Metadata);
        DurationSummary = FormatDuration(Metadata?.DurationSeconds);
        DimensionsSummary = Metadata is { WidthPixels: > 0, HeightPixels: > 0 }
            ? string.Format(CultureInfo.InvariantCulture, "{0}×{1}", Metadata.WidthPixels.Value, Metadata.HeightPixels.Value)
            : null;
        SummaryLine = BuildSummaryLine();
    }

    /// <summary>Underlying captured attachment.</summary>
    public CapturedMediaAttachment Attachment { get; }

    /// <summary>Rich capture metadata attached to the item, if any.</summary>
    public MediaCaptureMetadata? Metadata { get; }

    /// <summary>Structured annotation overlay attached to the item (empty when none).</summary>
    public PhotoAnnotationOverlay AnnotationOverlay { get; }

    /// <summary>Stable attachment identifier.</summary>
    public string AttachmentId => Attachment.AttachmentId;

    /// <summary>Host-local path to the media file.</summary>
    public string LocalPath => Attachment.LocalPath;

    /// <summary>File name portion of <see cref="LocalPath"/>.</summary>
    public string FileName => Path.GetFileName(Attachment.LocalPath);

    /// <summary>SDK media type (Photo, Video, Audio, Signature, Sketch, File).</summary>
    public FieldMediaType MediaType => Attachment.MediaType;

    /// <summary>Optional MIME content type.</summary>
    public string? ContentType => Attachment.ContentType;

    /// <summary>Optional file size in bytes.</summary>
    public long? SizeBytes => Attachment.SizeBytes;

    /// <summary>Optional user caption.</summary>
    public string? Caption => Metadata?.Caption;

    /// <summary>Whether the attachment carries a GPS geotag.</summary>
    public bool HasGeotag => GeotagSummary is not null;

    /// <summary>Formatted geotag string, or <see langword="null"/> when not geotagged.</summary>
    public string? GeotagSummary { get; }

    /// <summary>Number of markup annotations on this attachment.</summary>
    public int AnnotationCount => AnnotationOverlay.Count;

    /// <summary>Whether the attachment has one or more markup annotations.</summary>
    public bool HasAnnotations => AnnotationCount > 0;

    /// <summary>Whether on-image markup is supported for this attachment.</summary>
    public bool CanAnnotate => MediaType is FieldMediaType.Photo or FieldMediaType.Sketch;

    /// <summary>Formatted duration (e.g. <c>0:14</c>) for audio/video clips, or <see langword="null"/>.</summary>
    public string? DurationSummary { get; }

    /// <summary>Formatted pixel dimensions (e.g. <c>1600×1200</c>), or <see langword="null"/>.</summary>
    public string? DimensionsSummary { get; }

    /// <summary>Single-line human-readable summary combining type, duration/size, geotag, annotations, and caption.</summary>
    public string SummaryLine { get; }

    private string BuildSummaryLine()
    {
        var parts = new List<string> { MediaType.ToString() };

        if (DurationSummary is not null)
        {
            parts.Add(DurationSummary);
        }

        if (DimensionsSummary is not null)
        {
            parts.Add(DimensionsSummary);
        }

        if (HasAnnotations)
        {
            parts.Add(AnnotationCount == 1 ? "1 markup" : $"{AnnotationCount} markups");
        }

        if (GeotagSummary is not null)
        {
            parts.Add($"GPS {GeotagSummary}");
        }

        if (!string.IsNullOrWhiteSpace(Caption))
        {
            parts.Add($"\"{Caption}\"");
        }

        return string.Join(" · ", parts);
    }

    private static string? FormatDuration(double? seconds)
    {
        if (seconds is not { } s || !double.IsFinite(s) || s < 0)
        {
            return null;
        }

        var span = TimeSpan.FromSeconds(Math.Round(s, MidpointRounding.AwayFromZero));
        return span.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}", (int)span.TotalHours, span.Minutes, span.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", (int)span.TotalMinutes, span.Seconds);
    }
}
