// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Capture;

/// <summary>
/// Rich capture metadata attached to a <see cref="CapturedMediaAttachment"/> —
/// caption, clip duration, pixel dimensions, geotag altitude/bearing/accuracy,
/// and the unannotated source path when a flattened markup image was produced.
/// Stored in <see cref="CapturedMediaAttachment.EvidenceMetadata"/> under
/// <see cref="EvidenceKey"/> so the SDK contract stays portable while field
/// evidence retains full capture context.
/// </summary>
public sealed record MediaCaptureMetadata
{
    /// <summary>Evidence-metadata key under which <see cref="MediaCaptureMetadata"/> is stored.</summary>
    public const string EvidenceKey = "mediaMetadata";

    /// <summary>Optional user-entered caption or note describing the captured media.</summary>
    public string? Caption { get; init; }

    /// <summary>Duration of a recorded audio or video clip, in seconds.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Media width in pixels (photos and videos), when known.</summary>
    public int? WidthPixels { get; init; }

    /// <summary>Media height in pixels (photos and videos), when known.</summary>
    public int? HeightPixels { get; init; }

    /// <summary>Capture altitude in meters above WGS84 ellipsoid/sea level, when known.</summary>
    public double? AltitudeMeters { get; init; }

    /// <summary>Camera or device compass heading at capture time in degrees (0..360), when known.</summary>
    public double? BearingDegrees { get; init; }

    /// <summary>Horizontal GPS accuracy at capture time, in meters, when known.</summary>
    public double? AccuracyMeters { get; init; }

    /// <summary>
    /// Host-local path to the original unannotated image when the attachment's
    /// <see cref="CapturedMediaAttachment.LocalPath"/> points to a flattened annotated copy.
    /// </summary>
    public string? OriginalLocalPath { get; init; }

    /// <summary>
    /// Returns a validated copy of this metadata, normalizing bearing into [0, 360)
    /// and rejecting negative durations, negative accuracies, or non-positive dimensions.
    /// </summary>
    /// <returns>The normalized metadata.</returns>
    public MediaCaptureMetadata ValidateAndNormalize()
    {
        if (DurationSeconds is { } d && (double.IsNaN(d) || double.IsInfinity(d) || d < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds), "Duration cannot be negative or non-finite.");
        }

        if (WidthPixels is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WidthPixels), "Width must be positive.");
        }

        if (HeightPixels is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HeightPixels), "Height must be positive.");
        }

        if (AltitudeMeters is { } alt && !double.IsFinite(alt))
        {
            throw new ArgumentOutOfRangeException(nameof(AltitudeMeters), "Altitude must be finite.");
        }

        if (AccuracyMeters is { } acc && (double.IsNaN(acc) || double.IsInfinity(acc) || acc < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(AccuracyMeters), "Accuracy cannot be negative or non-finite.");
        }

        double? normalizedBearing = null;
        if (BearingDegrees is { } bearing)
        {
            if (double.IsNaN(bearing) || double.IsInfinity(bearing))
            {
                throw new ArgumentOutOfRangeException(nameof(BearingDegrees), "Bearing must be finite.");
            }

            var wrapped = bearing % 360.0;
            if (wrapped < 0)
            {
                wrapped += 360.0;
            }

            normalizedBearing = wrapped;
        }

        return this with
        {
            Caption = string.IsNullOrWhiteSpace(Caption) ? null : Caption.Trim(),
            BearingDegrees = normalizedBearing,
        };
    }

    /// <summary>
    /// Returns a copy with all geotag fields (<see cref="AltitudeMeters"/>,
    /// <see cref="BearingDegrees"/>, <see cref="AccuracyMeters"/>) cleared — used
    /// when a field's capture policy disables location capture.
    /// </summary>
    /// <returns>Metadata without geotag fields.</returns>
    public MediaCaptureMetadata WithoutGeotag() => this with
    {
        AltitudeMeters = null,
        BearingDegrees = null,
        AccuracyMeters = null,
    };

    /// <summary>
    /// Returns a copy of <paramref name="attachment"/> carrying this metadata in
    /// its <see cref="CapturedMediaAttachment.EvidenceMetadata"/>.
    /// </summary>
    /// <param name="attachment">Attachment to enrich.</param>
    /// <returns>An attachment carrying the normalized metadata.</returns>
    public CapturedMediaAttachment ApplyTo(CapturedMediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var normalized = ValidateAndNormalize();
        return attachment.WithEvidenceMetadata(
            new Dictionary<string, object?> { [EvidenceKey] = normalized });
    }

    /// <summary>Reads the rich capture metadata attached to an attachment, if any.</summary>
    /// <param name="attachment">Attachment to inspect.</param>
    /// <returns>The metadata, or <see langword="null"/> when none is present.</returns>
    public static MediaCaptureMetadata? From(CapturedMediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (!attachment.EvidenceMetadata.TryGetValue(EvidenceKey, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            MediaCaptureMetadata metadata => metadata,
            string json when !string.IsNullOrWhiteSpace(json) => FromJson(json),
            _ => null,
        };
    }

    /// <summary>Serializes this metadata to JSON for persistence.</summary>
    /// <returns>JSON representation.</returns>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Deserializes metadata from JSON produced by <see cref="ToJson"/>.</summary>
    /// <param name="json">JSON payload.</param>
    /// <returns>The deserialized metadata.</returns>
    public static MediaCaptureMetadata FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<MediaCaptureMetadata>(json)
            ?? throw new JsonException("MediaCaptureMetadata JSON deserialized to null.");
    }

    /// <summary>
    /// Formats a concise human-readable geotag summary (e.g.
    /// <c>21.31000, -157.81000 (±4.2m, 12m alt, 180°)</c>) from an attachment's
    /// location and optional metadata. Returns <see langword="null"/> when no
    /// location is present.
    /// </summary>
    /// <param name="location">Capture location, if any.</param>
    /// <param name="metadata">Optional rich metadata carrying altitude/bearing/accuracy.</param>
    /// <returns>Formatted geotag string, or <see langword="null"/>.</returns>
    public static string? FormatGeotag(FieldGeoPoint? location, MediaCaptureMetadata? metadata = null)
    {
        if (location is null)
        {
            return null;
        }

        var coords = string.Format(
            CultureInfo.InvariantCulture,
            "{0:F5}, {1:F5}",
            location.Latitude,
            location.Longitude);

        var details = new List<string>(3);
        var accuracy = metadata?.AccuracyMeters ?? location.AccuracyMeters;
        if (accuracy is { } acc)
        {
            details.Add(string.Format(CultureInfo.InvariantCulture, "±{0:F1}m", acc));
        }

        if (metadata?.AltitudeMeters is { } alt)
        {
            details.Add(string.Format(CultureInfo.InvariantCulture, "{0:F0}m alt", alt));
        }

        if (metadata?.BearingDegrees is { } bearing)
        {
            details.Add(string.Format(CultureInfo.InvariantCulture, "{0:F0}°", bearing));
        }

        return details.Count > 0 ? $"{coords} ({string.Join(", ", details)})" : coords;
    }
}
