// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Field;
using Honua.Field.Core.Field.Annotation;
using Honua.Field.Core.Field.Capture;
using Honua.Field.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Tests.Field.Capture;

public class MediaCaptureMetadataTests
{
    private static FormSession SessionWith(params FormField[] fields) => FormSession.CreateForNewRecord(
        new FormDefinition
        {
            FormId = "rich-media",
            Name = "Rich Media",
            Sections = [new FormSection { SectionId = "s", Label = "s", Fields = fields }],
        },
        "r1");

    [Fact]
    public void ValidateAndNormalize_wraps_bearing_and_trims_caption()
    {
        var meta = new MediaCaptureMetadata
        {
            Caption = "  Valve corrosion  ",
            DurationSeconds = 12.5,
            WidthPixels = 1600,
            HeightPixels = 1200,
            AltitudeMeters = 45.2,
            BearingDegrees = -45.0,
            AccuracyMeters = 3.5,
        }.ValidateAndNormalize();

        Assert.Equal("Valve corrosion", meta.Caption);
        Assert.Equal(315.0, meta.BearingDegrees);

        var blankCaption = new MediaCaptureMetadata { Caption = "   ", BearingDegrees = 405.0 }.ValidateAndNormalize();
        Assert.Null(blankCaption.Caption);
        Assert.Equal(45.0, blankCaption.BearingDegrees);
    }

    [Fact]
    public void ValidateAndNormalize_rejects_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaCaptureMetadata { DurationSeconds = -1 }.ValidateAndNormalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaCaptureMetadata { DurationSeconds = double.NaN }.ValidateAndNormalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaCaptureMetadata { WidthPixels = 0 }.ValidateAndNormalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaCaptureMetadata { HeightPixels = -10 }.ValidateAndNormalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaCaptureMetadata { AltitudeMeters = double.NaN }.ValidateAndNormalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaCaptureMetadata { AccuracyMeters = -0.1 }.ValidateAndNormalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaCaptureMetadata { BearingDegrees = double.PositiveInfinity }.ValidateAndNormalize());
    }

    [Fact]
    public void ApplyTo_and_From_round_trip_metadata_and_json()
    {
        var attachment = new CapturedMediaAttachment { AttachmentId = "a1", FieldId = "photo", LocalPath = "/tmp/p.jpg" };
        Assert.Null(MediaCaptureMetadata.From(attachment));

        var meta = new MediaCaptureMetadata
        {
            Caption = "North side",
            WidthPixels = 1920,
            HeightPixels = 1080,
            AltitudeMeters = 120,
            BearingDegrees = 180,
            AccuracyMeters = 4.2,
            OriginalLocalPath = "/tmp/orig.jpg",
        };

        var enriched = meta.ApplyTo(attachment);
        var readBack = MediaCaptureMetadata.From(enriched);
        Assert.NotNull(readBack);
        Assert.Equal("North side", readBack!.Caption);
        Assert.Equal(180, readBack.BearingDegrees);
        Assert.Equal("/tmp/orig.jpg", readBack.OriginalLocalPath);

        var fromJson = MediaCaptureMetadata.FromJson(meta.ToJson());
        Assert.Equal(meta, fromJson);

        var viaJsonMetadata = attachment.WithEvidenceMetadata(
            new Dictionary<string, object?> { [MediaCaptureMetadata.EvidenceKey] = meta.ToJson() });
        Assert.Equal(meta, MediaCaptureMetadata.From(viaJsonMetadata));
    }

    [Fact]
    public void FormatGeotag_formats_coordinates_and_optional_details()
    {
        Assert.Null(MediaCaptureMetadata.FormatGeotag(null));

        var basic = MediaCaptureMetadata.FormatGeotag(new FieldGeoPoint(21.31, -157.81));
        Assert.Equal("21.31000, -157.81000", basic);

        var full = MediaCaptureMetadata.FormatGeotag(
            new FieldGeoPoint(21.31, -157.81, 5.0),
            new MediaCaptureMetadata { AltitudeMeters = 12, BearingDegrees = 180, AccuracyMeters = 4.2 });
        Assert.Equal("21.31000, -157.81000 (±4.2m, 12m alt, 180°)", full);

        var pointAccuracyFallback = MediaCaptureMetadata.FormatGeotag(new FieldGeoPoint(21.31, -157.81, 6.5));
        Assert.Equal("21.31000, -157.81000 (±6.5m)", pointAccuracyFallback);
    }

    [Fact]
    public void MediaCaptureField_captures_rich_metadata_geotag_and_overlay_across_photo_video_audio()
    {
        var photoField = new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo };
        var videoField = new FormField { FieldId = "video", Label = "Video", Type = FormFieldType.Video };
        var audioField = new FormField { FieldId = "audio", Label = "Audio", Type = FormFieldType.Audio };
        var session = SessionWith(photoField, videoField, audioField);

        var overlay = new PhotoAnnotationOverlay([
            new PhotoAnnotation
            {
                Shape = AnnotationShape.Arrow,
                Points = [new AnnotationPoint(0.1, 0.2), new AnnotationPoint(0.8, 0.7)],
                Color = "#FF3B30",
            },
        ]);

        var photoWidget = new MediaCaptureField(session, photoField);
        var photo = photoWidget.Capture(
            "/tmp/photo.png",
            contentType: "image/png",
            sizeBytes: 2048,
            location: new FieldGeoPoint(21.31, -157.81, 3.2),
            metadata: new MediaCaptureMetadata { Caption = "Crack", WidthPixels = 1600, HeightPixels = 1200, BearingDegrees = 90 },
            annotationOverlay: overlay);

        Assert.Equal(FieldMediaType.Photo, photo.MediaType);
        Assert.Equal(1, PhotoAnnotationOverlay.From(photo).Count);
        Assert.Equal("Crack", MediaCaptureMetadata.From(photo)!.Caption);
        Assert.Equal(90, MediaCaptureMetadata.From(photo)!.BearingDegrees);

        var videoWidget = new MediaCaptureField(session, videoField);
        var video = videoWidget.Capture(
            "/tmp/clip.mp4",
            contentType: "video/mp4",
            sizeBytes: 500_000,
            location: new FieldGeoPoint(21.31, -157.81, 4.0),
            metadata: new MediaCaptureMetadata { DurationSeconds = 18.4, WidthPixels = 1280, HeightPixels = 720 });
        Assert.Equal(FieldMediaType.Video, video.MediaType);
        Assert.Equal(18.4, MediaCaptureMetadata.From(video)!.DurationSeconds);

        var audioWidget = new MediaCaptureField(session, audioField);
        var audio = audioWidget.Capture(
            "/tmp/note.wav",
            contentType: "audio/wav",
            sizeBytes: 44_100,
            location: new FieldGeoPoint(21.31, -157.81, 5.0),
            metadata: new MediaCaptureMetadata { DurationSeconds = 7.5, Caption = "Field note" });
        Assert.Equal(FieldMediaType.Audio, audio.MediaType);
        Assert.Equal(7.5, MediaCaptureMetadata.From(audio)!.DurationSeconds);
        Assert.Equal(3, session.Record.Media.Count);
    }

    [Fact]
    public void MediaCaptureField_strips_geotag_metadata_when_policy_disables_location()
    {
        var field = new FormField
        {
            FieldId = "photo",
            Label = "Photo",
            Type = FormFieldType.Photo,
            MediaPolicy = new FieldMediaCapturePolicy { CaptureLocation = false },
        };
        var session = SessionWith(field);
        var widget = new MediaCaptureField(session, field);

        var captured = widget.Capture(
            "/tmp/p.jpg",
            location: new FieldGeoPoint(21.31, -157.81, 3.0),
            metadata: new MediaCaptureMetadata
            {
                Caption = "Keep caption",
                AltitudeMeters = 100,
                BearingDegrees = 45,
                AccuracyMeters = 3.0,
            });

        Assert.Null(captured.CaptureLocation);
        var meta = MediaCaptureMetadata.From(captured)!;
        Assert.Equal("Keep caption", meta.Caption);
        Assert.Null(meta.AltitudeMeters);
        Assert.Null(meta.BearingDegrees);
        Assert.Null(meta.AccuracyMeters);
    }

    [Fact]
    public void Annotate_updates_overlay_preserves_original_path_and_rejects_audio()
    {
        var photoField = new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo };
        var audioField = new FormField { FieldId = "audio", Label = "Audio", Type = FormFieldType.Audio };
        var session = SessionWith(photoField, audioField);

        var photoWidget = new MediaCaptureField(session, photoField);
        var orig = photoWidget.Capture("/tmp/orig.jpg", contentType: "image/jpeg", sizeBytes: 1000);

        var overlay1 = new PhotoAnnotationOverlay([
            new PhotoAnnotation { Shape = AnnotationShape.Rectangle, Points = [new(0.1, 0.1), new(0.4, 0.4)] },
        ]);
        var annotated1 = photoWidget.Annotate(orig.AttachmentId, overlay1, "/tmp/ann1.png", "image/png", 1500);

        Assert.Equal("/tmp/ann1.png", annotated1.LocalPath);
        Assert.Equal("image/png", annotated1.ContentType);
        Assert.Equal(1500, annotated1.SizeBytes);
        Assert.Equal("/tmp/orig.jpg", MediaCaptureMetadata.From(annotated1)!.OriginalLocalPath);
        Assert.Equal(1, PhotoAnnotationOverlay.From(annotated1).Count);

        // Re-annotating preserves the very first OriginalLocalPath rather than overwriting it with /tmp/ann1.png.
        var overlay2 = new PhotoAnnotationOverlay([
            new PhotoAnnotation { Shape = AnnotationShape.Text, Points = [new(0.5, 0.5)], Text = "Leak" },
        ]);
        var annotated2 = photoWidget.Annotate(orig.AttachmentId, overlay2, "/tmp/ann2.png", "image/png", 1600);
        Assert.Equal("/tmp/orig.jpg", MediaCaptureMetadata.From(annotated2)!.OriginalLocalPath);
        Assert.Equal("Leak", PhotoAnnotationOverlay.From(annotated2).Annotations[0].Text);
        Assert.Single(session.Record.Media);
        Assert.Equal("ann2.png", session.Record.Media[0].FileName);

        // Audio fields cannot be annotated.
        var audioWidget = new MediaCaptureField(session, audioField);
        var clip = audioWidget.Capture("/tmp/a.wav", contentType: "audio/wav");
        Assert.Throws<InvalidOperationException>(() => audioWidget.Annotate(clip.AttachmentId, overlay1));
        Assert.Throws<KeyNotFoundException>(() => photoWidget.Annotate("missing", overlay1));
    }

    [Fact]
    public void UpdateMetadata_and_ReplaceMedia_work_in_form_and_repeat_rows()
    {
        var photoField = new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo };
        var form = new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections =
            [
                new FormSection { SectionId = "items", Label = "Items", Repeatable = true, Fields = [photoField] },
            ],
        };
        var session = FormSession.CreateForNewRecord(form, "r1");
        var row = session.AddRepeatInstance("items");
        var widget = new MediaCaptureField(row, photoField);

        var item = widget.Capture("/tmp/p.jpg", contentType: "image/jpeg");
        var updated = widget.UpdateMetadata(item.AttachmentId, new MediaCaptureMetadata { Caption = "Updated note" });

        Assert.Equal("Updated note", MediaCaptureMetadata.From(updated)!.Caption);
        Assert.Equal("Updated note", MediaCaptureMetadata.From(widget.Attachments[0])!.Caption);
        Assert.False(row.ReplaceMedia(item with { AttachmentId = "nonexistent" }));
    }
}
