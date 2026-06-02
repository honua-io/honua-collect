using Honua.Collect.Core.Field.Capture;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Capture;

public class CaptureWidgetTests
{
    private static FormField PhotoField(FieldMediaCapturePolicy? policy = null, FieldValidationRule? validation = null) => new()
    {
        FieldId = "photo",
        Label = "Photo",
        Type = FormFieldType.Photo,
        MediaPolicy = policy,
        Validation = validation ?? new FieldValidationRule(),
    };

    private static FormSession SessionWith(params FormField[] fields) => FormSession.CreateForNewRecord(
        new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections = [new FormSection { SectionId = "s", Label = "s", Fields = fields }],
        },
        "r1");

    [Fact]
    public void Widget_kind_maps_field_types()
    {
        Assert.Equal(CaptureWidgetKind.Photo, CaptureWidget.For(FormFieldType.Photo));
        Assert.Equal(CaptureWidgetKind.Barcode, CaptureWidget.For(FormFieldType.Barcode));
        Assert.Equal(CaptureWidgetKind.Choice, CaptureWidget.For(FormFieldType.MultipleChoice));
        Assert.Equal(CaptureWidgetKind.Toggle, CaptureWidget.For(FormFieldType.YesNo));
        Assert.Equal(CaptureWidgetKind.Text, CaptureWidget.For(FormFieldType.Address));
    }

    [Fact]
    public void Media_widget_rejects_non_media_fields()
    {
        var session = SessionWith(new FormField { FieldId = "t", Label = "T", Type = FormFieldType.Text });
        Assert.Throws<ArgumentException>(() => new MediaCaptureField(session, session.GetField("t").Field));
    }

    [Fact]
    public void Capture_adds_an_attachment_and_stamps_policy_metadata()
    {
        var field = PhotoField(new FieldMediaCapturePolicy { CaptureLocation = true, RequiresFaceBlur = true });
        var session = SessionWith(field);
        var widget = new MediaCaptureField(session, field);

        var a = widget.Capture("/tmp/p.jpg", contentType: "image/jpeg", sizeBytes: 100,
            location: new FieldGeoPoint(1, 2));

        Assert.Equal(1, widget.Count);
        Assert.True(a.RequiresFaceBlur);
        Assert.NotNull(a.CaptureLocation);
        Assert.Single(session.Record.Media); // mirrored into the record
    }

    [Fact]
    public void Capture_drops_location_when_policy_disallows_it()
    {
        var field = PhotoField(new FieldMediaCapturePolicy { CaptureLocation = false });
        var session = SessionWith(field);
        var widget = new MediaCaptureField(session, field);

        var a = widget.Capture("/tmp/p.jpg", location: new FieldGeoPoint(1, 2));
        Assert.Null(a.CaptureLocation);
    }

    [Fact]
    public void Capture_enforces_max_count_content_type_and_size()
    {
        var field = PhotoField(
            new FieldMediaCapturePolicy { AllowedContentTypes = ["image/jpeg"], MaxAttachmentBytes = 50 },
            new FieldValidationRule { MaxMediaCount = 1 });
        var session = SessionWith(field);
        var widget = new MediaCaptureField(session, field);

        Assert.Throws<ArgumentException>(() => widget.Capture("/tmp/p.png", contentType: "image/png", sizeBytes: 10));
        Assert.Throws<ArgumentException>(() => widget.Capture("/tmp/p.jpg", contentType: "image/jpeg", sizeBytes: 100));

        widget.Capture("/tmp/p.jpg", contentType: "image/jpeg", sizeBytes: 10);
        Assert.False(widget.CanCaptureMore);
        Assert.Throws<InvalidOperationException>(() => widget.Capture("/tmp/q.jpg", contentType: "image/jpeg", sizeBytes: 10));
    }

    [Fact]
    public void Remove_deletes_an_attachment()
    {
        var field = PhotoField();
        var session = SessionWith(field);
        var widget = new MediaCaptureField(session, field);
        var a = widget.Capture("/tmp/p.jpg");

        Assert.True(widget.Remove(a.AttachmentId));
        Assert.Equal(0, widget.Count);
    }

    [Fact]
    public void Barcode_widget_writes_and_clears_a_scan_value()
    {
        var field = new FormField { FieldId = "code", Label = "Code", Type = FormFieldType.Barcode, Required = true };
        var session = SessionWith(field);
        var widget = new BarcodeCaptureField(session, field);

        Assert.False(session.CanSubmit); // required, unscanned
        var scan = widget.Scan("12345", format: "CODE_128");

        Assert.True(widget.HasValue);
        Assert.Equal("12345", scan.Value);
        Assert.Equal("CODE_128", widget.Current!.Format);
        Assert.True(session.CanSubmit); // scan satisfies required

        widget.Clear();
        Assert.False(widget.HasValue);
    }

    [Fact]
    public void Media_widget_works_inside_a_repeat_row()
    {
        var photo = PhotoField();
        var form = new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections =
            [
                new FormSection { SectionId = "items", Label = "Items", Repeatable = true, Fields = [photo] },
            ],
        };
        var session = FormSession.CreateForNewRecord(form, "r1");
        var row = session.AddRepeatInstance("items");

        var widget = new MediaCaptureField(row, photo);
        widget.Capture("/tmp/p.jpg");

        Assert.Equal(1, widget.Count);
    }
}
