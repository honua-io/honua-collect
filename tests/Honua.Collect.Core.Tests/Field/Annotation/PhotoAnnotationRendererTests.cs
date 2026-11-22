// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Annotation;
using SkiaSharp;

namespace Honua.Collect.Core.Tests.Field.Annotation;

public class PhotoAnnotationRendererTests
{
    private static byte[] CreateBlankPng(int width, int height, SKColor fill)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(fill);
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void RenderToPng_draws_all_five_annotation_shapes_and_preserves_dimensions()
    {
        var source = CreateBlankPng(200, 120, SKColors.White);
        var overlay = new PhotoAnnotationOverlay([
            new PhotoAnnotation
            {
                Shape = AnnotationShape.Freehand,
                Points = [new(0.05, 0.1), new(0.2, 0.1), new(0.2, 0.25)],
                Color = "#FF0000",
                StrokeWidth = 0.02,
            },
            new PhotoAnnotation
            {
                Shape = AnnotationShape.Freehand,
                Points = [new(0.05, 0.9)], // single-point dot
                Color = "#0000FF",
                StrokeWidth = 0.03,
            },
            new PhotoAnnotation
            {
                Shape = AnnotationShape.Arrow,
                Points = [new(0.3, 0.2), new(0.7, 0.2)],
                Color = "#00AA00",
                StrokeWidth = 0.02,
            },
            new PhotoAnnotation
            {
                Shape = AnnotationShape.Rectangle,
                Points = [new(0.3, 0.4), new(0.6, 0.7)],
                Color = "#000000",
                StrokeWidth = 0.02,
            },
            new PhotoAnnotation
            {
                Shape = AnnotationShape.Ellipse,
                Points = [new(0.65, 0.4), new(0.95, 0.7)],
                Color = "#FFCC00",
                StrokeWidth = 0.02,
            },
            new PhotoAnnotation
            {
                Shape = AnnotationShape.Text,
                Points = [new(0.1, 0.85)],
                Color = "#FFFFFF",
                Text = "Crack #1",
            },
        ]);

        var rendered = PhotoAnnotationRenderer.RenderToPng(source, overlay);
        Assert.NotEmpty(rendered);

        using var decoded = SKBitmap.Decode(rendered);
        Assert.NotNull(decoded);
        Assert.Equal(200, decoded!.Width);
        Assert.Equal(120, decoded.Height);

        // Verify pixels were actually drawn (no longer pure white across the image).
        var nonWhitePixels = 0;
        for (var y = 0; y < decoded.Height; y++)
        {
            for (var x = 0; x < decoded.Width; x++)
            {
                if (decoded.GetPixel(x, y) != SKColors.White)
                {
                    nonWhitePixels++;
                }
            }
        }

        Assert.True(nonWhitePixels > 100, $"Expected markup pixels to be drawn, got {nonWhitePixels}.");
    }

    [Fact]
    public void RenderToFile_writes_annotated_png_and_keeps_source_unchanged()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"honua-ann-{Guid.NewGuid():n}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcPath = Path.Combine(tempDir, "src.png");
            var dstPath = Path.Combine(tempDir, "out.png");
            var sourceBytes = CreateBlankPng(80, 60, SKColors.White);
            File.WriteAllBytes(srcPath, sourceBytes);

            var overlay = new PhotoAnnotationOverlay([
                new PhotoAnnotation
                {
                    Shape = AnnotationShape.Arrow,
                    Points = [new(0.1, 0.5), new(0.9, 0.5)],
                    Color = "not-a-hex", // falls back cleanly to red
                },
                new PhotoAnnotation
                {
                    Shape = AnnotationShape.Freehand,
                    Points = [], // empty points skipped cleanly
                },
            ]);

            PhotoAnnotationRenderer.RenderToFile(srcPath, dstPath, overlay);

            Assert.Equal(sourceBytes, File.ReadAllBytes(srcPath));
            Assert.True(File.Exists(dstPath));
            using var decoded = SKBitmap.Decode(dstPath);
            Assert.Equal(80, decoded.Width);
            Assert.Equal(60, decoded.Height);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RenderToPng_rejects_empty_or_invalid_image_bytes()
    {
        var overlay = new PhotoAnnotationOverlay();
        Assert.Throws<ArgumentException>(() => PhotoAnnotationRenderer.RenderToPng([], overlay));
        Assert.Throws<InvalidDataException>(() => PhotoAnnotationRenderer.RenderToPng([1, 2, 3, 4], overlay));
    }

    [Fact]
    public void RenderToFile_writes_jpeg_for_a_jpg_destination()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"honua-ann-{Guid.NewGuid():n}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcPath = Path.Combine(tempDir, "src.png");
            var dstPath = Path.Combine(tempDir, "out.jpg");
            File.WriteAllBytes(srcPath, CreateBlankPng(64, 48, SKColors.White));
            var overlay = new PhotoAnnotationOverlay([
                new PhotoAnnotation { Shape = AnnotationShape.Rectangle, Points = [new(0.2, 0.2), new(0.8, 0.8)] },
            ]);

            PhotoAnnotationRenderer.RenderToFile(srcPath, dstPath, overlay);

            using var codec = SKCodec.Create(dstPath);
            Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
            Assert.Equal(64, codec.Info.Width);
            Assert.Equal(48, codec.Info.Height);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void RenderToJpeg_rejects_quality_out_of_range(int quality)
    {
        var source = CreateBlankPng(8, 8, SKColors.White);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PhotoAnnotationRenderer.RenderToJpeg(source, new PhotoAnnotationOverlay(), quality));
    }

    [Fact]
    public void RenderToPng_skips_degenerate_shapes_without_drawing()
    {
        var source = CreateBlankPng(40, 30, SKColors.White);
        var overlay = new PhotoAnnotationOverlay([
            new PhotoAnnotation { Shape = AnnotationShape.Arrow, Points = [new(0.5, 0.5), new(0.5, 0.5)] },
            new PhotoAnnotation { Shape = AnnotationShape.Arrow, Points = [new(0.2, 0.2)] },
            new PhotoAnnotation { Shape = AnnotationShape.Rectangle, Points = [new(0.2, 0.2)] },
            new PhotoAnnotation { Shape = AnnotationShape.Text, Points = [new(0.2, 0.8)], Text = "  " },
        ]);

        using var decoded = SKBitmap.Decode(PhotoAnnotationRenderer.RenderToPng(source, overlay));

        var nonWhitePixels = 0;
        for (var y = 0; y < decoded.Height; y++)
        {
            for (var x = 0; x < decoded.Width; x++)
            {
                if (decoded.GetPixel(x, y) != SKColors.White)
                {
                    nonWhitePixels++;
                }
            }
        }

        // Only the zero-length arrow's round-capped line dot may be drawn.
        Assert.True(nonWhitePixels < 40, $"Expected degenerate shapes to draw almost nothing, got {nonWhitePixels}.");
    }
}
