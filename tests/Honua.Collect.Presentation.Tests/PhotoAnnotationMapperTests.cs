using Honua.Collect.Core.Field.Annotation;
using Honua.Collect.Presentation.Field;

namespace Honua.Collect.Presentation.Tests;

public class PhotoAnnotationMapperTests
{
    [Fact]
    public void ToFreehand_normalizes_display_points_to_0_1()
    {
        var stroke = new (double X, double Y)[] { (0, 0), (100, 50), (200, 100) };

        var annotation = PhotoAnnotationMapper.ToFreehand(stroke, displayWidth: 200, displayHeight: 100);

        Assert.Equal(AnnotationShape.Freehand, annotation.Shape);
        Assert.Equal(3, annotation.Points.Count);
        Assert.Equal(new AnnotationPoint(0, 0), annotation.Points[0]);
        Assert.Equal(new AnnotationPoint(0.5, 0.5), annotation.Points[1]);
        Assert.Equal(new AnnotationPoint(1.0, 1.0), annotation.Points[2]);
    }

    [Fact]
    public void Normalize_clamps_overshoot_into_bounds()
    {
        // A touch dragged past the right/bottom edge stays a valid 0..1 coordinate.
        var point = PhotoAnnotationMapper.Normalize(250, -10, displayWidth: 200, displayHeight: 100);

        Assert.Equal(new AnnotationPoint(1.0, 0.0), point);
    }

    [Fact]
    public void Normalize_is_safe_for_zero_size()
    {
        var point = PhotoAnnotationMapper.Normalize(10, 10, displayWidth: 0, displayHeight: 0);

        Assert.Equal(new AnnotationPoint(0, 0), point);
    }

    [Fact]
    public void Display_to_normalized_to_pixels_round_trips_at_a_different_size()
    {
        // Captured at 200x100 on screen, re-rendered at the image's native 800x400:
        // the same relative position must come back scaled 4x.
        var stroke = new (double X, double Y)[] { (50, 25) };

        var annotation = PhotoAnnotationMapper.ToFreehand(stroke, 200, 100);
        var pixels = PhotoAnnotationMapper.ToPixels(annotation, 800, 400);

        Assert.Single(pixels);
        Assert.Equal(200, pixels[0].X, 6);
        Assert.Equal(100, pixels[0].Y, 6);
    }

    [Fact]
    public void ToOverlay_skips_empty_strokes_and_empty_input_yields_empty_overlay()
    {
        var strokes = new List<IReadOnlyList<(double X, double Y)>>
        {
            new List<(double, double)>(),               // empty stroke - skipped
            new List<(double, double)> { (10, 10) },    // kept
        };

        var overlay = PhotoAnnotationMapper.ToOverlay(strokes, 100, 100);
        Assert.Equal(1, overlay.Count);

        var empty = PhotoAnnotationMapper.ToOverlay(
            new List<IReadOnlyList<(double X, double Y)>>(), 100, 100);
        Assert.Equal(0, empty.Count);
    }

    [Fact]
    public void ToOverlay_carries_color_and_stroke_width()
    {
        var strokes = new List<IReadOnlyList<(double X, double Y)>>
        {
            new List<(double, double)> { (0, 0), (50, 50) },
        };

        var overlay = PhotoAnnotationMapper.ToOverlay(strokes, 100, 100, color: "#FFCC00", strokeWidth: 0.01);

        Assert.Equal("#FFCC00", overlay.Annotations[0].Color);
        Assert.Equal(0.01, overlay.Annotations[0].StrokeWidth);
    }

    [Fact]
    public void StrokeWidthPixels_scales_by_smaller_dimension_with_a_floor()
    {
        var annotation = new PhotoAnnotation
        {
            Shape = AnnotationShape.Freehand,
            Points = [new AnnotationPoint(0, 0)],
            StrokeWidth = 0.01,
        };

        // smaller dimension 400 -> 0.01 * 400 = 4px
        Assert.Equal(4.0, PhotoAnnotationMapper.StrokeWidthPixels(annotation, 800, 400), 6);

        // tiny target never goes below 1px
        Assert.Equal(1.0, PhotoAnnotationMapper.StrokeWidthPixels(annotation, 10, 10), 6);
    }

    [Fact]
    public void ToPixels_of_empty_annotation_is_empty()
    {
        var annotation = new PhotoAnnotation { Shape = AnnotationShape.Freehand, Points = [] };

        Assert.Empty(PhotoAnnotationMapper.ToPixels(annotation, 800, 400));
    }
}
