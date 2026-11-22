// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Annotation;

namespace Honua.Collect.Presentation.Field;

public static class PhotoAnnotationMapper
{
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

    public static IReadOnlyList<(double X, double Y)> ToPixels(
        PhotoAnnotation annotation, double targetWidth, double targetHeight)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return annotation.Points
            .Select(p => (p.X * targetWidth, p.Y * targetHeight))
            .ToList();
    }

    public static double StrokeWidthPixels(
        PhotoAnnotation annotation, double targetWidth, double targetHeight)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var smaller = Math.Min(Math.Abs(targetWidth), Math.Abs(targetHeight));
        return Math.Max(1.0, annotation.StrokeWidth * smaller);
    }

    public static AnnotationPoint Normalize(double x, double y, double displayWidth, double displayHeight)
    {
        var nx = displayWidth > 0 ? x / displayWidth : 0.0;
        var ny = displayHeight > 0 ? y / displayHeight : 0.0;
        return new AnnotationPoint(Math.Clamp(nx, 0.0, 1.0), Math.Clamp(ny, 0.0, 1.0));
    }
}
