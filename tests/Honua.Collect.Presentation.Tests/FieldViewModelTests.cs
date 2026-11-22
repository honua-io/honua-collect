// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Honua.Collect.Core.Field.Annotation;
using Honua.Collect.Core.Field.Capture;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

public class FieldViewModelTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "inspection",
        Name = "Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields =
                [
                    new FormField
                    {
                        FieldId = "name",
                        Label = "Name",
                        Type = FormFieldType.Text,
                        Required = true,
                        HelpText = "Your full name",
                    },
                    new FormField { FieldId = "count", Label = "Count", Type = FormFieldType.Numeric },
                    new FormField { FieldId = "hasDamage", Label = "Damage?", Type = FormFieldType.YesNo },
                    new FormField
                    {
                        FieldId = "severity",
                        Label = "Severity",
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "low", Label = "Low" },
                            new FieldChoice { Value = "high", Label = "High" },
                        ],
                    },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                    new FormField { FieldId = "video", Label = "Video", Type = FormFieldType.Video },
                    new FormField { FieldId = "audio", Label = "Audio", Type = FormFieldType.Audio },
                    new FormField { FieldId = "sig", Label = "Sign", Type = FormFieldType.Signature },
                    new FormField { FieldId = "code", Label = "Code", Type = FormFieldType.Barcode },
                    new FormField
                    {
                        FieldId = "notes",
                        Label = "Notes",
                        Type = FormFieldType.Text,
                        Required = true,
                        VisibilityRule = new FieldVisibilityRule
                        {
                            DependsOnFieldId = "hasDamage",
                            Operator = ComparisonOperator.Equals,
                            MatchValue = true,
                        },
                    },
                ],
            },
        ],
    };

    private static FormPageViewModel NewPage() => new(FormSession.CreateForNewRecord(Form(), "r1"));

    private static FieldViewModel Field(FormPageViewModel page, string id) =>
        page.Fields.Single(f => f.FieldId == id);

    [Fact]
    public void Exposes_definition_metadata()
    {
        var page = NewPage();
        var name = Field(page, "name");

        Assert.Equal("Name", name.Label);
        Assert.Equal("Your full name", name.HelpText);
        Assert.True(name.IsRequired);
        Assert.Equal(CaptureWidgetKind.Text, name.Widget);
    }

    [Theory]
    [InlineData("photo", "Add photo")]
    [InlineData("sig", "Sign")]
    [InlineData("code", "Scan barcode")]
    [InlineData("name", "Add attachment")] // non-media falls through to default
    public void CaptureActionLabel_varies_by_widget(string fieldId, string expected)
        => Assert.Equal(expected, Field(NewPage(), fieldId).CaptureActionLabel);

    [Fact]
    public void Setting_value_pushes_through_and_invokes_changed_callback()
    {
        var page = NewPage();
        var name = Field(page, "name");

        name.Value = "Ada";

        // The value is pushed through the host (read back through the same state).
        Assert.Equal("Ada", name.Value);
        Assert.Equal("Ada", page.Session.GetField("name").Value);
    }

    [Fact]
    public void Setting_value_to_equal_value_is_a_no_op()
    {
        var page = NewPage();
        var name = Field(page, "name");
        name.Value = "Ada";

        var changed = 0;
        name.PropertyChanged += (_, _) => changed++;
        // Re-assigning the identical value must not push through (Equals short-circuit).
        name.Value = "Ada";

        Assert.Equal(0, changed);
        Assert.Equal("Ada", name.Value);
    }

    [Fact]
    public void SelectedChoice_round_trips_value_and_choice()
    {
        var page = NewPage();
        var severity = Field(page, "severity");

        Assert.Null(severity.SelectedChoice);
        Assert.Equal(2, severity.Choices.Count);

        severity.SelectedChoice = severity.Choices.Single(c => c.Value == "high");

        Assert.Equal("high", severity.Value);
        Assert.Equal("high", severity.SelectedChoice!.Value);
    }

    [Fact]
    public void SelectedChoice_set_to_null_clears_value()
    {
        var page = NewPage();
        var severity = Field(page, "severity");
        severity.SelectedChoice = severity.Choices[0];
        Assert.NotNull(severity.Value);

        severity.SelectedChoice = null;

        Assert.Null(severity.Value);
        Assert.Null(severity.SelectedChoice);
    }

    [Fact]
    public void Visibility_and_required_reflect_runtime_state()
    {
        var page = NewPage();
        var notes = Field(page, "notes");
        Assert.False(notes.IsVisible);

        Field(page, "hasDamage").Value = true;
        notes.Refresh();

        Assert.True(notes.IsVisible);
    }

    [Fact]
    public void Error_text_and_has_error_track_validation()
    {
        var page = NewPage();
        var name = Field(page, "name");
        // Required + empty after a submit pass yields a validation error.
        page.Session.Submit();
        name.Refresh();

        Assert.True(name.HasError);
        Assert.NotEmpty(name.ErrorText);

        name.Value = "Ada";
        page.Session.Submit();
        name.Refresh();

        Assert.False(name.HasError);
        Assert.Empty(name.ErrorText);
    }

    [Fact]
    public void CaptureMedia_registers_attachment_and_increments_count()
    {
        var page = NewPage();
        var photo = Field(page, "photo");
        Assert.Equal(0, photo.MediaCount);

        photo.CaptureMedia("/tmp/a.jpg", "image/jpeg");

        Assert.Equal(1, photo.MediaCount);
    }

    [Fact]
    public void CaptureMedia_rejects_blank_path()
        => Assert.Throws<ArgumentException>(() => Field(NewPage(), "photo").CaptureMedia(" "));

    [Fact]
    public void Refresh_raises_change_notification_for_bound_members()
    {
        var page = NewPage();
        var name = Field(page, "name");
        var changed = new List<string?>();
        name.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        name.Refresh();

        Assert.Contains(nameof(FieldViewModel.Value), changed);
        Assert.Contains(nameof(FieldViewModel.IsVisible), changed);
        Assert.Contains(nameof(FieldViewModel.HasError), changed);
        Assert.Contains(nameof(FieldViewModel.MediaCount), changed);
    }

    [Fact]
    public void Constructor_rejects_null_host_and_callback()
    {
        Assert.Throws<ArgumentNullException>(() => new FieldViewModel(null!, "x", () => { }));
        var page = NewPage();
        var host = FormSession.CreateForNewRecord(Form(), "r2");
        Assert.Throws<ArgumentNullException>(() => new FieldViewModel(host, "name", null!));
    }

    [Fact]
    public void CaptureMedia_stamps_media_type_geotag_metadata_and_annotations_and_supports_edit_and_remove()
    {
        var page = NewPage();
        var photo = Field(page, "photo");
        var video = Field(page, "video");
        var audio = Field(page, "audio");

        Assert.False(photo.HasMedia);
        Assert.True(photo.CanCaptureMore);

        var initialOverlay = new PhotoAnnotationOverlay([
            new PhotoAnnotation { Shape = AnnotationShape.Arrow, Points = [new(0.1, 0.1), new(0.9, 0.9)] },
        ]);

        var capturedPhoto = photo.CaptureMedia(
            "/tmp/site.png",
            contentType: "image/png",
            sizeBytes: 4096,
            location: new FieldGeoPoint(21.31, -157.81, 3.5),
            metadata: new MediaCaptureMetadata { Caption = "Front gate", WidthPixels = 1600, HeightPixels = 1200, AltitudeMeters = 15, BearingDegrees = 90 },
            annotationOverlay: initialOverlay);

        Assert.True(photo.HasMedia);
        var photoItem = Assert.Single(photo.MediaItems);
        Assert.Equal(FieldMediaType.Photo, photoItem.MediaType);
        Assert.Equal("site.png", photoItem.FileName);
        Assert.Equal("image/png", photoItem.ContentType);
        Assert.Equal(4096, photoItem.SizeBytes);
        Assert.True(photoItem.HasGeotag);
        Assert.True(photoItem.HasAnnotations);
        Assert.Equal(1, photoItem.AnnotationCount);
        Assert.True(photoItem.CanAnnotate);
        Assert.Equal("1600×1200", photoItem.DimensionsSummary);
        Assert.Contains("1 markup", photoItem.SummaryLine);
        Assert.Contains("GPS 21.31000, -157.81000", photoItem.SummaryLine);
        Assert.Contains("\"Front gate\"", photoItem.SummaryLine);

        // Re-annotate and update caption
        var updatedOverlay = new PhotoAnnotationOverlay([
            new PhotoAnnotation { Shape = AnnotationShape.Arrow, Points = [new(0.1, 0.1), new(0.9, 0.9)] },
            new PhotoAnnotation { Shape = AnnotationShape.Text, Points = [new(0.5, 0.5)], Text = "Crack" },
        ]);
        photo.AnnotateMedia(capturedPhoto.AttachmentId, updatedOverlay, "/tmp/site-v2.png", "image/png", 5000);
        photo.UpdateMediaCaption(capturedPhoto.AttachmentId, "Updated caption");

        var editedItem = Assert.Single(photo.MediaItems);
        Assert.Equal("/tmp/site-v2.png", editedItem.LocalPath);
        Assert.Equal(2, editedItem.AnnotationCount);
        Assert.Contains("2 markups", editedItem.SummaryLine);
        Assert.Equal("Updated caption", editedItem.Caption);

        // Video and audio capture preserve MediaType, geotag, and duration
        video.CaptureMedia(
            "/tmp/walk.mp4",
            contentType: "video/mp4",
            sizeBytes: 120_000,
            location: new FieldGeoPoint(21.31, -157.81),
            metadata: new MediaCaptureMetadata { DurationSeconds = 3661 });
        var videoItem = Assert.Single(video.MediaItems);
        Assert.Equal(FieldMediaType.Video, videoItem.MediaType);
        Assert.False(videoItem.CanAnnotate);
        Assert.Equal("1:01:01", videoItem.DurationSummary);
        Assert.Contains("1:01:01", videoItem.SummaryLine);

        audio.CaptureMedia(
            "/tmp/memo.wav",
            contentType: "audio/wav",
            sizeBytes: 22_000,
            location: new FieldGeoPoint(21.31, -157.81),
            metadata: new MediaCaptureMetadata { DurationSeconds = 14 });
        var audioItem = Assert.Single(audio.MediaItems);
        Assert.Equal(FieldMediaType.Audio, audioItem.MediaType);
        Assert.Equal("0:14", audioItem.DurationSummary);

        // Remove media
        Assert.True(photo.RemoveMedia(capturedPhoto.AttachmentId));
        Assert.False(photo.RemoveMedia("nonexistent"));
        Assert.False(photo.HasMedia);
        Assert.Empty(photo.MediaItems);
    }
}
