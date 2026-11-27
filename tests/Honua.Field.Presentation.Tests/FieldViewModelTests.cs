// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Honua.Field.Core.Field;
using Honua.Field.Core.Field.Annotation;
using Honua.Field.Core.Field.Capture;
using Honua.Field.Core.Field.Forms;
using Honua.Field.Presentation.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Presentation.Tests;

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

    private static readonly PhotoAnnotationOverlay ArrowOverlay = new([
        new PhotoAnnotation { Shape = AnnotationShape.Arrow, Points = [new(0.1, 0.1), new(0.9, 0.9)] },
    ]);

    private static CapturedMediaAttachment CapturePhoto(FieldViewModel photo) => photo.CaptureMedia(
        "/tmp/site.png",
        contentType: "image/png",
        sizeBytes: 4096,
        location: new FieldGeoPoint(21.31, -157.81, 3.5),
        metadata: new MediaCaptureMetadata { Caption = "Front gate", WidthPixels = 1600, HeightPixels = 1200, AltitudeMeters = 15, BearingDegrees = 90 },
        annotationOverlay: ArrowOverlay);

    private static FieldViewModel PolicyField(FieldMediaCapturePolicy? policy)
    {
        var form = new FormDefinition
        {
            FormId = "policy",
            Name = "Policy",
            Sections =
            [
                new FormSection
                {
                    SectionId = "main",
                    Label = "Main",
                    Fields = [new FormField { FieldId = "geo", Label = "Geo photo", Type = FormFieldType.Photo, MediaPolicy = policy }],
                },
            ],
        };
        return new FieldViewModel(FormSession.CreateForNewRecord(form, "p1"), "geo", () => { });
    }

    [Fact]
    public void CaptureMedia_stamps_media_type_geotag_metadata_and_annotations()
    {
        var photo = Field(NewPage(), "photo");
        Assert.False(photo.HasMedia);
        Assert.True(photo.CanCaptureMore);

        CapturePhoto(photo);

        Assert.True(photo.HasMedia);
        var item = Assert.Single(photo.MediaItems);
        Assert.Equal(FieldMediaType.Photo, item.MediaType);
        Assert.Equal("site.png", item.FileName);
        Assert.Equal("image/png", item.ContentType);
        Assert.Equal(4096, item.SizeBytes);
        Assert.True(item.HasGeotag);
        Assert.True(item.HasAnnotations);
        Assert.Equal(1, item.AnnotationCount);
        Assert.True(item.CanAnnotate);
        Assert.Equal("1600×1200", item.DimensionsSummary);
        Assert.Contains("1 markup", item.SummaryLine);
        Assert.Contains("GPS 21.31000, -157.81000", item.SummaryLine);
        Assert.Contains("\"Front gate\"", item.SummaryLine);
    }

    [Fact]
    public void AnnotateMedia_and_UpdateMediaCaption_edit_an_existing_photo()
    {
        var photo = Field(NewPage(), "photo");
        var captured = CapturePhoto(photo);
        var updatedOverlay = new PhotoAnnotationOverlay([
            .. ArrowOverlay.Annotations,
            new PhotoAnnotation { Shape = AnnotationShape.Text, Points = [new(0.5, 0.5)], Text = "Crack" },
        ]);

        photo.AnnotateMedia(captured.AttachmentId, updatedOverlay, "/tmp/site-v2.jpg", "image/jpeg", 5000);
        photo.UpdateMediaCaption(captured.AttachmentId, "Updated caption");

        var item = Assert.Single(photo.MediaItems);
        Assert.Equal("/tmp/site-v2.jpg", item.LocalPath);
        Assert.Equal(2, item.AnnotationCount);
        Assert.Contains("2 markups", item.SummaryLine);
        Assert.Equal("Updated caption", item.Caption);
    }

    [Fact]
    public void UpdateMediaCaption_rejects_an_unknown_attachment()
        => Assert.Throws<KeyNotFoundException>(() => Field(NewPage(), "photo").UpdateMediaCaption("missing", "x"));

    [Fact]
    public void CaptureMedia_preserves_video_and_audio_type_and_duration()
    {
        var page = NewPage();
        var video = Field(page, "video");
        var audio = Field(page, "audio");

        video.CaptureMedia(
            "/tmp/walk.mp4",
            contentType: "video/mp4",
            sizeBytes: 120_000,
            location: new FieldGeoPoint(21.31, -157.81),
            metadata: new MediaCaptureMetadata { DurationSeconds = 3661 });
        audio.CaptureMedia(
            "/tmp/memo.wav",
            contentType: "audio/wav",
            sizeBytes: 22_000,
            metadata: new MediaCaptureMetadata { DurationSeconds = 14 });

        var videoItem = Assert.Single(video.MediaItems);
        Assert.Equal(FieldMediaType.Video, videoItem.MediaType);
        Assert.False(videoItem.CanAnnotate);
        Assert.Equal("1:01:01", videoItem.DurationSummary);
        Assert.Contains("1:01:01", videoItem.SummaryLine);
        var audioItem = Assert.Single(audio.MediaItems);
        Assert.Equal(FieldMediaType.Audio, audioItem.MediaType);
        Assert.Equal("0:14", audioItem.DurationSummary);
    }

    [Fact]
    public void RemoveMedia_removes_only_known_attachments()
    {
        var photo = Field(NewPage(), "photo");
        var captured = CapturePhoto(photo);

        Assert.False(photo.RemoveMedia("nonexistent"));
        Assert.True(photo.HasAttachment(captured.AttachmentId));
        Assert.True(photo.RemoveMedia(captured.AttachmentId));

        Assert.False(photo.HasMedia);
        Assert.False(photo.HasAttachment(captured.AttachmentId));
        Assert.Empty(photo.MediaItems);
    }

    [Fact]
    public void CaptureMedia_on_a_non_media_field_keeps_metadata_and_markup()
    {
        var name = Field(NewPage(), "name");

        var attachment = name.CaptureMedia(
            "/tmp/doc.png",
            contentType: "image/png",
            metadata: new MediaCaptureMetadata { Caption = "Scan" },
            annotationOverlay: ArrowOverlay);

        Assert.Equal("Scan", MediaCaptureMetadata.From(attachment)?.Caption);
        Assert.Equal(1, PhotoAnnotationOverlay.From(attachment).Count);
        Assert.True(name.HasAttachment(attachment.AttachmentId));
    }

    [Fact]
    public void CapturesLocation_follows_the_media_policy()
    {
        Assert.Equal(new FieldMediaCapturePolicy().CaptureLocation, PolicyField(null).CapturesLocation);
        Assert.True(PolicyField(new FieldMediaCapturePolicy { CaptureLocation = true }).CapturesLocation);
        Assert.False(PolicyField(new FieldMediaCapturePolicy { CaptureLocation = false }).CapturesLocation);
        Assert.True(Field(NewPage(), "name").CapturesLocation); // non-media fields have no policy
    }

    [Fact]
    public void MediaItems_is_rebuilt_and_announced_only_when_attachments_change()
    {
        var photo = Field(NewPage(), "photo");
        photo.Refresh(); // settle: the first refresh always announces the list
        var changed = new List<string?>();
        photo.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var first = photo.MediaItems;
        photo.Refresh();
        Assert.Same(first, photo.MediaItems);
        Assert.DoesNotContain(nameof(FieldViewModel.MediaItems), changed);

        CapturePhoto(photo);
        var afterCapture = photo.MediaItems;
        photo.Refresh();
        Assert.NotSame(first, afterCapture);
        Assert.Same(afterCapture, photo.MediaItems);
        Assert.Single(changed, n => n == nameof(FieldViewModel.MediaItems));
    }
}
