using Honua.Collect.Core.Field.Annotation;

namespace Honua.Collect.Presentation.Field;

/// <summary>
/// Pure, device-free mapping between freehand strokes captured at a display size
/// and the normalized <see cref="PhotoAnnotation"/>/<see cref="PhotoAnnotationOverlay"/>
/// model (BACKLOG C7 — Fulcrum parity). Strokes are drawn over a photo shown in a
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
}
