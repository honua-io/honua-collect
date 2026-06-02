namespace Honua.Collect.Core.Field.Annotation;

/// <summary>The kind of markup drawn on a photo (BACKLOG C7 — Fulcrum parity).</summary>
public enum AnnotationShape
{
    /// <summary>An arrow from the first point to the last.</summary>
    Arrow,

    /// <summary>A rectangle bounded by the first and last point.</summary>
    Rectangle,

    /// <summary>An ellipse bounded by the first and last point.</summary>
    Ellipse,

    /// <summary>A freehand path through all points.</summary>
    Freehand,

    /// <summary>A text label anchored at the first point.</summary>
    Text,
}

/// <summary>
/// A point in normalized image coordinates (0..1 of width/height) so annotations
/// are independent of the photo's pixel resolution.
/// </summary>
/// <param name="X">Horizontal position, 0 (left) to 1 (right).</param>
/// <param name="Y">Vertical position, 0 (top) to 1 (bottom).</param>
public readonly record struct AnnotationPoint(double X, double Y);

/// <summary>
/// A single markup element drawn on a captured photo. Coordinates are normalized
/// so the markup renders correctly at any display size and survives downscaling.
/// </summary>
public sealed record PhotoAnnotation
{
    /// <summary>The shape to draw.</summary>
    public required AnnotationShape Shape { get; init; }

    /// <summary>Points defining the shape, in normalized image coordinates.</summary>
    public IReadOnlyList<AnnotationPoint> Points { get; init; } = [];

    /// <summary>Stroke/fill color as a hex string (e.g. <c>#FF0000</c>).</summary>
    public string Color { get; init; } = "#FF0000";

    /// <summary>Stroke width as a fraction of the image's smaller dimension.</summary>
    public double StrokeWidth { get; init; } = 0.005;

    /// <summary>Label text for <see cref="AnnotationShape.Text"/> annotations.</summary>
    public string? Text { get; init; }
}
