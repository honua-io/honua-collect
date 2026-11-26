// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Field.Annotation;

namespace Honua.Field.Presentation.Field;

/// <summary>
/// Pure, device-free mapping between freehand strokes captured at a display size
/// and the normalized <see cref="PhotoAnnotation"/>/<see cref="PhotoAnnotationOverlay"/>
/// model (BACKLOG C7). Strokes are drawn over a photo shown in a
/// <c>GraphicsView</c> at some pixel size; this maps those display points into
/// 0..1 image coordinates (and back) so the markup renders correctly at any size
/// and survives downscaling. The page keeps only the platform rasterization; all
/// the coordinate math lives here so it is unit-testable without a device.
/// </summary>
public static class PhotoAnnotationMapper
{
    /// <summary>
    /// Builds a normalized freehand <see cref="PhotoAnnotation"/> from a stroke of
    /// display-space points captured over an image shown at the given pixel size.
    /// </summary>
    /// <param name="strokePoints">The stroke's points in display pixels.</param>
    /// <param name="displayWidth">Display width the stroke was drawn at, in pixels.</param>
    /// <param name="displayHeight">Display height the stroke was drawn at, in pixels.</param>
    /// <param name="color">Stroke color as a hex string (e.g. <c>#FF0000</c>).</param>
    /// <param name="strokeWidth">Stroke width as a fraction of the smaller image dimension.</param>
    /// <returns>The normalized freehand annotation.</returns>
    public static PhotoAnnotation ToFreehand(
        IEnumerable<(double X, double Y)> strokePoints,
        double displayWidth,
        double displayHeight,
        string color = "#FF0000",
        double strokeWidth = 0.005)
    {
        ArgumentNullException.ThrowIfNull(strokePoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);

        var points = strokePoints
            .Select(p => Normalize(p.X, p.Y, displayWidth, displayHeight))
            .ToList();

        return new PhotoAnnotation
        {
            Shape = AnnotationShape.Freehand,
            Points = points,
            Color = color,
            StrokeWidth = strokeWidth,
        };
    }

    /// <summary>
    /// Builds an overlay from a set of freehand strokes captured at a display size.
    /// Empty strokes (and an empty set) are skipped, so an empty drawing yields an
    /// empty overlay rather than throwing.
    /// </summary>
    /// <param name="strokes">Each stroke's display-space points.</param>
    /// <param name="displayWidth">Display width the strokes were drawn at, in pixels.</param>
    /// <param name="displayHeight">Display height the strokes were drawn at, in pixels.</param>
    /// <param name="color">Stroke color as a hex string.</param>
    /// <param name="strokeWidth">Stroke width as a fraction of the smaller image dimension.</param>
    /// <returns>The normalized overlay.</returns>
    public static PhotoAnnotationOverlay ToOverlay(
        IEnumerable<IReadOnlyList<(double X, double Y)>> strokes,
        double displayWidth,
        double displayHeight,
        string color = "#FF0000",
        double strokeWidth = 0.005)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        var overlay = new PhotoAnnotationOverlay();
        foreach (var stroke in strokes)
        {
            if (stroke is null || stroke.Count == 0)
            {
                continue;
            }

            overlay.Add(ToFreehand(stroke, displayWidth, displayHeight, color, strokeWidth));
        }

        return overlay;
    }

    /// <summary>
    /// Projects a normalized annotation's points back into pixel coordinates for a
    /// target render size (e.g. the source image's pixel dimensions when flattening,
    /// or the display size when re-drawing for edit).
    /// </summary>
    /// <param name="annotation">The annotation to project.</param>
    /// <param name="targetWidth">Target width in pixels.</param>
    /// <param name="targetHeight">Target height in pixels.</param>
    /// <returns>The annotation's points in target pixel coordinates.</returns>
    public static IReadOnlyList<(double X, double Y)> ToPixels(
        PhotoAnnotation annotation, double targetWidth, double targetHeight)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return annotation.Points
            .Select(p => (p.X * targetWidth, p.Y * targetHeight))
            .ToList();
    }

    /// <summary>
    /// Resolves the absolute stroke width in pixels for a render size, from an
    /// annotation's <see cref="PhotoAnnotation.StrokeWidth"/> fraction (which is a
    /// fraction of the smaller dimension). Never returns less than 1px.
    /// </summary>
    /// <param name="annotation">The annotation.</param>
    /// <param name="targetWidth">Target width in pixels.</param>
    /// <param name="targetHeight">Target height in pixels.</param>
    /// <returns>The stroke width in pixels.</returns>
    public static double StrokeWidthPixels(
        PhotoAnnotation annotation, double targetWidth, double targetHeight)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var smaller = Math.Min(Math.Abs(targetWidth), Math.Abs(targetHeight));
        return Math.Max(1.0, annotation.StrokeWidth * smaller);
    }

    /// <summary>
    /// Builds a normalized <see cref="AnnotationShape.Arrow"/> annotation from
    /// display-space start and end points.
    /// </summary>
    /// <param name="start">Arrow tail in display pixels.</param>
    /// <param name="end">Arrowhead tip in display pixels.</param>
    /// <param name="displayWidth">Display width in pixels.</param>
    /// <param name="displayHeight">Display height in pixels.</param>
    /// <param name="color">Stroke color as a hex string.</param>
    /// <param name="strokeWidth">Stroke width as a fraction of the smaller dimension.</param>
    /// <returns>The normalized arrow annotation.</returns>
    public static PhotoAnnotation ToArrow(
        (double X, double Y) start,
        (double X, double Y) end,
        double displayWidth,
        double displayHeight,
        string color = "#FF0000",
        double strokeWidth = 0.005)
        => ToTwoPointShape(AnnotationShape.Arrow, start, end, displayWidth, displayHeight, color, strokeWidth);

    /// <summary>
    /// Builds a normalized <see cref="AnnotationShape.Rectangle"/> annotation from
    /// two opposite corners in display pixels.
    /// </summary>
    /// <param name="start">First corner in display pixels.</param>
    /// <param name="end">Opposite corner in display pixels.</param>
    /// <param name="displayWidth">Display width in pixels.</param>
    /// <param name="displayHeight">Display height in pixels.</param>
    /// <param name="color">Stroke color as a hex string.</param>
    /// <param name="strokeWidth">Stroke width as a fraction of the smaller dimension.</param>
    /// <returns>The normalized rectangle annotation.</returns>
    public static PhotoAnnotation ToRectangle(
        (double X, double Y) start,
        (double X, double Y) end,
        double displayWidth,
        double displayHeight,
        string color = "#FF0000",
        double strokeWidth = 0.005)
        => ToTwoPointShape(AnnotationShape.Rectangle, start, end, displayWidth, displayHeight, color, strokeWidth);

    /// <summary>
    /// Builds a normalized <see cref="AnnotationShape.Ellipse"/> annotation bounded
    /// by two opposite corners in display pixels.
    /// </summary>
    /// <param name="start">First bounding corner in display pixels.</param>
    /// <param name="end">Opposite bounding corner in display pixels.</param>
    /// <param name="displayWidth">Display width in pixels.</param>
    /// <param name="displayHeight">Display height in pixels.</param>
    /// <param name="color">Stroke color as a hex string.</param>
    /// <param name="strokeWidth">Stroke width as a fraction of the smaller dimension.</param>
    /// <returns>The normalized ellipse annotation.</returns>
    public static PhotoAnnotation ToEllipse(
        (double X, double Y) start,
        (double X, double Y) end,
        double displayWidth,
        double displayHeight,
        string color = "#FF0000",
        double strokeWidth = 0.005)
        => ToTwoPointShape(AnnotationShape.Ellipse, start, end, displayWidth, displayHeight, color, strokeWidth);

    /// <summary>
    /// Builds a normalized <see cref="AnnotationShape.Text"/> callout anchored at
    /// a display-space point.
    /// </summary>
    /// <param name="anchor">Anchor point in display pixels.</param>
    /// <param name="text">Callout label text.</param>
    /// <param name="displayWidth">Display width in pixels.</param>
    /// <param name="displayHeight">Display height in pixels.</param>
    /// <param name="color">Text color as a hex string.</param>
    /// <param name="strokeWidth">Relative scale factor.</param>
    /// <returns>The normalized text annotation.</returns>
    public static PhotoAnnotation ToText(
        (double X, double Y) anchor,
        string text,
        double displayWidth,
        double displayHeight,
        string color = "#FF0000",
        double strokeWidth = 0.005)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);

        return new PhotoAnnotation
        {
            Shape = AnnotationShape.Text,
            Points = [Normalize(anchor.X, anchor.Y, displayWidth, displayHeight)],
            Color = color,
            StrokeWidth = strokeWidth,
            Text = text.Trim(),
            // Capture the editor's minimum once; applying it again at native
            // resolution would shrink the label relative to its preview.
            FontSize = Math.Max(14.0 / Math.Max(1.0, Math.Min(displayWidth, displayHeight)), strokeWidth * 5),
        };
    }

    /// <summary>
    /// Computes the axis-aligned pixel bounding box for a two-point shape
    /// (<see cref="AnnotationShape.Rectangle"/> or <see cref="AnnotationShape.Ellipse"/>),
    /// handling drags in any direction.
    /// </summary>
    /// <param name="annotation">Annotation with at least two points.</param>
    /// <param name="targetWidth">Target width in pixels.</param>
    /// <param name="targetHeight">Target height in pixels.</param>
    /// <returns>Origin and non-negative dimensions in target pixels.</returns>
    public static (double X, double Y, double Width, double Height) ToBoundsRect(
        PhotoAnnotation annotation, double targetWidth, double targetHeight)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.Points.Count < 2)
        {
            return (0, 0, 0, 0);
        }

        var x1 = annotation.Points[0].X * targetWidth;
        var y1 = annotation.Points[0].Y * targetHeight;
        var x2 = annotation.Points[^1].X * targetWidth;
        var y2 = annotation.Points[^1].Y * targetHeight;

        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        return (left, top, Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    /// <summary>
    /// Computes the two arrowhead wing endpoints in pixels for an arrow pointing
    /// from <paramref name="start"/> to <paramref name="end"/>. Returns
    /// <see langword="null"/> when the arrow has near-zero length.
    /// </summary>
    /// <param name="start">Arrow tail in pixels.</param>
    /// <param name="end">Arrowhead tip in pixels.</param>
    /// <param name="headLengthPx">Wing length in pixels.</param>
    /// <returns>Left and right wing tip coordinates, or <see langword="null"/> for degenerate arrows.</returns>
    public static ((double X, double Y) Left, (double X, double Y) Right)? ComputeArrowHead(
        (double X, double Y) start,
        (double X, double Y) end,
        double headLengthPx)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-3 || headLengthPx <= 0)
        {
            return null;
        }

        var effectiveLen = Math.Min(headLengthPx, length * 0.5);
        var angle = Math.Atan2(dy, dx);
        const double spread = 0.45; // ~26 degrees

        var left = (end.X - (effectiveLen * Math.Cos(angle - spread)), end.Y - (effectiveLen * Math.Sin(angle - spread)));
        var right = (end.X - (effectiveLen * Math.Cos(angle + spread)), end.Y - (effectiveLen * Math.Sin(angle + spread)));
        return (left, right);
    }

    /// <summary>
    /// Normalizes a display-space point into 0..1 image coordinates, clamped to the
    /// image bounds so an overshoot (a touch dragged past the edge) stays valid.
    /// </summary>
    /// <param name="x">Display x in pixels.</param>
    /// <param name="y">Display y in pixels.</param>
    /// <param name="displayWidth">Display width in pixels.</param>
    /// <param name="displayHeight">Display height in pixels.</param>
    /// <returns>The normalized point.</returns>
    public static AnnotationPoint Normalize(double x, double y, double displayWidth, double displayHeight)
    {
        var nx = displayWidth > 0 ? x / displayWidth : 0.0;
        var ny = displayHeight > 0 ? y / displayHeight : 0.0;
        return new AnnotationPoint(Math.Clamp(nx, 0.0, 1.0), Math.Clamp(ny, 0.0, 1.0));
    }

    private static PhotoAnnotation ToTwoPointShape(
        AnnotationShape shape,
        (double X, double Y) start,
        (double X, double Y) end,
        double displayWidth,
        double displayHeight,
        string color,
        double strokeWidth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);

        return new PhotoAnnotation
        {
            Shape = shape,
            Points =
            [
                Normalize(start.X, start.Y, displayWidth, displayHeight),
                Normalize(end.X, end.Y, displayWidth, displayHeight),
            ],
            Color = color,
            StrokeWidth = strokeWidth,
        };
    }
}
