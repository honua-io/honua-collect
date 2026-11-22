// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

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

    [Fact]
    public void ToArrow_ToRectangle_ToEllipse_and_ToText_normalize_all_shapes()
    {
        var arrow = PhotoAnnotationMapper.ToArrow((20, 10), (180, 90), 200, 100, "#00FF00", 0.01);
        Assert.Equal(AnnotationShape.Arrow, arrow.Shape);
        Assert.Equal(2, arrow.Points.Count);
        Assert.Equal(new AnnotationPoint(0.1, 0.1), arrow.Points[0]);
        Assert.Equal(new AnnotationPoint(0.9, 0.9), arrow.Points[1]);
        Assert.Equal("#00FF00", arrow.Color);

        var rect = PhotoAnnotationMapper.ToRectangle((180, 90), (20, 10), 200, 100);
        Assert.Equal(AnnotationShape.Rectangle, rect.Shape);
        var bounds = PhotoAnnotationMapper.ToBoundsRect(rect, 400, 200);
        Assert.Equal(40, bounds.X, 6);
        Assert.Equal(20, bounds.Y, 6);
        Assert.Equal(320, bounds.Width, 6);
        Assert.Equal(160, bounds.Height, 6);

        var ellipse = PhotoAnnotationMapper.ToEllipse((50, 25), (150, 75), 200, 100);
        Assert.Equal(AnnotationShape.Ellipse, ellipse.Shape);

        var text = PhotoAnnotationMapper.ToText((100, 50), "  Crack  ", 200, 100, "#FFCC00");
        Assert.Equal(AnnotationShape.Text, text.Shape);
        Assert.Equal("Crack", text.Text);
        Assert.Equal(new AnnotationPoint(0.5, 0.5), text.Points[0]);
        Assert.Throws<ArgumentException>(() => PhotoAnnotationMapper.ToText((10, 10), "   ", 200, 100));
    }

    [Fact]
    public void ToBoundsRect_returns_zero_when_fewer_than_two_points()
    {
        var single = new PhotoAnnotation { Shape = AnnotationShape.Rectangle, Points = [new(0.5, 0.5)] };
        Assert.Equal((0, 0, 0, 0), PhotoAnnotationMapper.ToBoundsRect(single, 100, 100));
    }

    [Fact]
    public void ComputeArrowHead_returns_symmetric_wings_and_null_for_degenerate_arrows()
    {
        var wings = PhotoAnnotationMapper.ComputeArrowHead((0, 0), (100, 0), headLengthPx: 20);
        Assert.NotNull(wings);
        Assert.True(wings!.Value.Left.X < 100 && wings.Value.Right.X < 100);
        Assert.Equal(wings.Value.Left.X, wings.Value.Right.X, 6);
        Assert.Equal(-wings.Value.Left.Y, wings.Value.Right.Y, 6);

        Assert.Null(PhotoAnnotationMapper.ComputeArrowHead((10, 10), (10, 10), 20));
        Assert.Null(PhotoAnnotationMapper.ComputeArrowHead((0, 0), (100, 0), 0));
    }
}
