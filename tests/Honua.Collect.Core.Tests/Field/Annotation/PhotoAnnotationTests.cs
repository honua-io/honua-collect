using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Annotation;

namespace Honua.Collect.Core.Tests.Field.Annotation;

public class PhotoAnnotationTests
{
    private static PhotoAnnotation Arrow() => new()
    {
        Shape = AnnotationShape.Arrow,
        Points = [new AnnotationPoint(0.1, 0.1), new AnnotationPoint(0.5, 0.5)],
        Color = "#00FF00",
    };

    [Fact]
    public void Overlay_adds_and_undoes_annotations()
    {
        var overlay = new PhotoAnnotationOverlay();
        overlay.Add(Arrow());
        overlay.Add(new PhotoAnnotation { Shape = AnnotationShape.Text, Points = [new AnnotationPoint(0.2, 0.2)], Text = "Crack" });

        Assert.Equal(2, overlay.Count);
        Assert.True(overlay.Undo());
        Assert.Equal(1, overlay.Count);
    }

    [Fact]
    public void ApplyTo_attaches_overlay_without_touching_the_image_path()
    {
        var attachment = new CapturedMediaAttachment { AttachmentId = "a1", FieldId = "photo", LocalPath = "/p.jpg" };
        var overlay = new PhotoAnnotationOverlay();
        overlay.Add(Arrow());

        var annotated = overlay.ApplyTo(attachment);

        Assert.Equal("/p.jpg", annotated.LocalPath); // original image untouched
        var roundTrip = PhotoAnnotationOverlay.From(annotated);
        Assert.Equal(1, roundTrip.Count);
        Assert.Equal(AnnotationShape.Arrow, roundTrip.Annotations[0].Shape);
        Assert.Equal("#00FF00", roundTrip.Annotations[0].Color);
    }

    [Fact]
    public void From_returns_empty_overlay_when_none_attached()
    {
        var attachment = new CapturedMediaAttachment { AttachmentId = "a1", LocalPath = "/p.jpg" };
        Assert.Equal(0, PhotoAnnotationOverlay.From(attachment).Count);
    }

    [Fact]
    public void Json_round_trips_annotations()
    {
        var overlay = new PhotoAnnotationOverlay();
        overlay.Add(Arrow());
        overlay.Add(new PhotoAnnotation { Shape = AnnotationShape.Rectangle, Points = [new AnnotationPoint(0, 0), new AnnotationPoint(1, 1)] });

        var restored = PhotoAnnotationOverlay.FromJson(overlay.ToJson());

        Assert.Equal(2, restored.Count);
        Assert.Equal(AnnotationShape.Rectangle, restored.Annotations[1].Shape);
        Assert.Equal(new AnnotationPoint(1, 1), restored.Annotations[1].Points[1]);
    }
}
