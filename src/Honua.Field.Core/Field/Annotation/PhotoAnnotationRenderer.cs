// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Imaging;
using SkiaSharp;

namespace Honua.Field.Core.Field.Annotation;

/// <summary>
/// Headless SkiaSharp renderer that flattens a <see cref="PhotoAnnotationOverlay"/>
/// onto an image (supporting <see cref="AnnotationShape.Freehand"/>,
/// <see cref="AnnotationShape.Arrow"/>, <see cref="AnnotationShape.Rectangle"/>,
/// <see cref="AnnotationShape.Ellipse"/>, and <see cref="AnnotationShape.Text"/>).
/// Runs without a UI or device so markup flattening is testable in Core CI and
/// reusable in exports/reports.
/// </summary>
public static class PhotoAnnotationRenderer
{
    /// <summary>
    /// Renders <paramref name="overlay"/> on top of the encoded image bytes in
    /// <paramref name="sourceImageBytes"/> and returns the flattened PNG bytes.
    /// </summary>
    /// <param name="sourceImageBytes">Encoded source image (JPEG, PNG, WebP, etc.).</param>
    /// <param name="overlay">Annotations to burn onto the image.</param>
    /// <returns>PNG-encoded bytes of the annotated image.</returns>
    public static byte[] RenderToPng(ReadOnlySpan<byte> sourceImageBytes, PhotoAnnotationOverlay overlay) =>
        Render(sourceImageBytes, overlay, SKEncodedImageFormat.Png, 100);

    /// <summary>
    /// Renders <paramref name="overlay"/> on top of the encoded image bytes and
    /// returns the flattened image as JPEG, so an annotated photo keeps the
    /// size and content type of a compressed capture.
    /// </summary>
    /// <param name="sourceImageBytes">Encoded source image (JPEG, PNG, WebP, etc.).</param>
    /// <param name="overlay">Annotations to burn onto the image.</param>
    /// <param name="quality">JPEG quality, 1-100.</param>
    /// <returns>JPEG-encoded bytes of the annotated image.</returns>
    public static byte[] RenderToJpeg(ReadOnlySpan<byte> sourceImageBytes, PhotoAnnotationOverlay overlay, int quality = 85)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        return Render(sourceImageBytes, overlay, SKEncodedImageFormat.Jpeg, quality);
    }

    private static byte[] Render(
        ReadOnlySpan<byte> sourceImageBytes,
        PhotoAnnotationOverlay overlay,
        SKEncodedImageFormat format,
        int quality)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (sourceImageBytes.IsEmpty)
        {
            throw new ArgumentException("Source image bytes cannot be empty.", nameof(sourceImageBytes));
        }

        using var data = SKData.CreateCopy(sourceImageBytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Could not decode source image bytes.");
        var origin = codec.EncodedOrigin;
        using var decoded = SKBitmap.Decode(codec)
            ?? throw new InvalidDataException("Could not decode source image bytes.");
        using var bitmap = SkiaImageResizer.ApplyOrientation(decoded, origin);

        var width = bitmap.Width;
        var height = bitmap.Height;
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Failed to allocate Skia drawing surface.");

        var canvas = surface.Canvas;
        using var sourceImage = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(sourceImage, 0, 0, SKSamplingOptions.Default);

        foreach (var annotation in overlay.Annotations)
        {
            DrawAnnotation(canvas, annotation, width, height);
        }

        using var image = surface.Snapshot();
        using var encoded = image.Encode(format, quality)
            ?? throw new InvalidOperationException($"Failed to encode annotated {format} image.");

        return encoded.ToArray();
    }

    /// <summary>
    /// Renders <paramref name="overlay"/> onto <paramref name="sourcePath"/> and
    /// writes the flattened image to <paramref name="destinationPath"/>: JPEG when
    /// the destination ends in <c>.jpg</c> or <c>.jpeg</c>, PNG otherwise.
    /// </summary>
    /// <param name="sourcePath">Path to the source image file.</param>
    /// <param name="destinationPath">Path where the flattened image will be written.</param>
    /// <param name="overlay">Annotations to burn onto the image.</param>
    public static void RenderToFile(string sourcePath, string destinationPath, PhotoAnnotationOverlay overlay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(overlay);

        var bytes = File.ReadAllBytes(sourcePath);
        var extension = Path.GetExtension(destinationPath);
        var rendered = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? RenderToJpeg(bytes, overlay)
                : RenderToPng(bytes, overlay);
        File.WriteAllBytes(destinationPath, rendered);
    }

    private static void DrawAnnotation(SKCanvas canvas, PhotoAnnotation annotation, int width, int height)
    {
        if (annotation.Points.Count == 0)
        {
            return;
        }

        var color = ParseColor(annotation.Color);
        var strokeWidth = (float)Math.Max(1.0, annotation.StrokeWidth * Math.Min(width, height));

        using var strokePaint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        switch (annotation.Shape)
        {
            case AnnotationShape.Freehand:
                DrawFreehand(canvas, annotation.Points, width, height, strokePaint, color, strokeWidth);
                break;

            case AnnotationShape.Arrow when annotation.Points.Count >= 2:
                DrawArrow(canvas, annotation.Points[0], annotation.Points[^1], width, height, strokePaint, strokeWidth);
                break;

            case AnnotationShape.Rectangle when annotation.Points.Count >= 2:
                canvas.DrawRect(ToRect(annotation.Points[0], annotation.Points[^1], width, height), strokePaint);
                break;

            case AnnotationShape.Ellipse when annotation.Points.Count >= 2:
                canvas.DrawOval(ToRect(annotation.Points[0], annotation.Points[^1], width, height), strokePaint);
                break;

            case AnnotationShape.Text when !string.IsNullOrWhiteSpace(annotation.Text):
                DrawText(canvas, annotation.Points[0], annotation.Text, width, height, color, (float)annotation.TextSizePixels(width, height));
                break;
        }
    }

    private static void DrawFreehand(
        SKCanvas canvas,
        IReadOnlyList<AnnotationPoint> points,
        int width,
        int height,
        SKPaint strokePaint,
        SKColor color,
        float strokeWidth)
    {
        if (points.Count == 1)
        {
            using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)(points[0].X * width), (float)(points[0].Y * height), strokeWidth / 2f, fill);
            return;
        }

        using var builder = new SKPathBuilder();
        builder.MoveTo((float)(points[0].X * width), (float)(points[0].Y * height));
        for (var i = 1; i < points.Count; i++)
        {
            builder.LineTo((float)(points[i].X * width), (float)(points[i].Y * height));
        }

        using var path = builder.Detach();
        canvas.DrawPath(path, strokePaint);
    }

    private static void DrawArrow(
        SKCanvas canvas,
        AnnotationPoint start,
        AnnotationPoint end,
        int width,
        int height,
        SKPaint strokePaint,
        float strokeWidth)
    {
        var x1 = (float)(start.X * width);
        var y1 = (float)(start.Y * height);
        var x2 = (float)(end.X * width);
        var y2 = (float)(end.Y * height);

        canvas.DrawLine(x1, y1, x2, y2, strokePaint);

        var dx = x2 - x1;
        var dy = y2 - y1;
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-3f)
        {
            return;
        }

        var headLen = MathF.Min(strokeWidth * 6f, length * 0.5f);
        var angle = MathF.Atan2(dy, dx);
        const float spread = 0.45f; // ~26 degrees

        var leftX = x2 - (headLen * MathF.Cos(angle - spread));
        var leftY = y2 - (headLen * MathF.Sin(angle - spread));
        var rightX = x2 - (headLen * MathF.Cos(angle + spread));
        var rightY = y2 - (headLen * MathF.Sin(angle + spread));

        canvas.DrawLine(x2, y2, leftX, leftY, strokePaint);
        canvas.DrawLine(x2, y2, rightX, rightY, strokePaint);
    }

    private static void DrawText(
        SKCanvas canvas,
        AnnotationPoint anchor,
        string text,
        int width,
        int height,
        SKColor color,
        float fontSize)
    {
        var x = (float)(anchor.X * width);
        var y = (float)(anchor.Y * height);

        using var font = new SKFont { Size = fontSize };
        var textWidth = font.MeasureText(text);
        var pad = fontSize * 0.25f;
        var bgRect = new SKRect(x - pad, y - fontSize - pad, x + textWidth + pad, y + (pad * 1.5f));

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawRoundRect(bgRect, 4f, 4f, bgPaint);

        using var textPaint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, textPaint);
    }

    private static SKRect ToRect(AnnotationPoint a, AnnotationPoint b, int width, int height)
    {
        var x1 = (float)(a.X * width);
        var y1 = (float)(a.Y * height);
        var x2 = (float)(b.X * width);
        var y2 = (float)(b.Y * height);
        return new SKRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    private static SKColor ParseColor(string hex) =>
        SKColor.TryParse(hex, out var parsed) ? parsed : SKColors.Red;
}
