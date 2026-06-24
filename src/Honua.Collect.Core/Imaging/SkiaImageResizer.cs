using SkiaSharp;

namespace Honua.Collect.Core.Imaging;

/// <summary>
/// The headless, SkiaSharp-backed <see cref="IImageResizer"/> that runs the C8
/// resize/compress budget on real bytes (BACKLOG C8). It decodes the source,
/// normalises EXIF orientation so a phone-rotated photo lands upright, downscales
/// the longest edge to <see cref="ImageResizeOptions.MaxEdge"/> with high-quality
/// sampling, and re-encodes at the target codec/quality. When the source is already
/// within the dimension budget, already in the target format, and
/// <see cref="ImageResizeOptions.SkipWhenWithinBudget"/> is set, the source bytes are
/// returned verbatim to avoid a needless re-encode.
/// </summary>
public sealed class SkiaImageResizer : IImageResizer
{
    /// <inheritdoc />
    public ImageResizeResult Resize(byte[] source, ImageResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (source.Length == 0)
        {
            throw new ArgumentException("Source image bytes are empty.", nameof(source));
        }

        options.Validate();

        using var data = SKData.CreateCopy(source);

        // Read the EXIF orientation BEFORE decoding pixels — SKBitmap.Decode drops it.
        var orientation = ReadOrientation(data);

        using var codecBitmap = SKBitmap.Decode(data)
            ?? throw new ImageDecodeException("Image bytes could not be decoded.");

        using var upright = ApplyOrientation(codecBitmap, orientation);

        var plan = ImageResizePlan.For(upright.Width, upright.Height, options.MaxEdge);
        var alreadyTargetFormat = MatchesTargetFormat(data, options.Format);

        // No-op fast path: within the dimension budget, no orientation rewrite was
        // needed, and the source is already in the target codec.
        if (!plan.ResizeNeeded
            && orientation is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default
            && alreadyTargetFormat
            && options.SkipWhenWithinBudget)
        {
            return new ImageResizeResult(
                source, upright.Width, upright.Height, options.Format, Resized: false, ReEncoded: false);
        }

        using var scaled = plan.ResizeNeeded
            ? upright.Resize(new SKImageInfo(plan.TargetWidth, plan.TargetHeight), Sampling)
            : null;
        var output = scaled ?? upright;

        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(ToSkFormat(options.Format), options.Quality)
            ?? throw new ImageDecodeException($"Image could not be encoded as {options.Format}.");

        return new ImageResizeResult(
            encoded.ToArray(),
            output.Width,
            output.Height,
            options.Format,
            Resized: plan.ResizeNeeded,
            ReEncoded: true);
    }

    private static readonly SKSamplingOptions Sampling =
        new(SKCubicResampler.Mitchell);

    private static SKEncodedOrigin ReadOrientation(SKData data)
    {
        using var codec = SKCodec.Create(data);
        return codec?.EncodedOrigin ?? SKEncodedOrigin.TopLeft;
    }

    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return Copy(source);
        }

        // Orientations 5–8 swap the axes; the output canvas dimensions flip.
        var swap = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;

        var dstWidth = swap ? source.Height : source.Width;
        var dstHeight = swap ? source.Width : source.Height;

        var result = new SKBitmap(dstWidth, dstHeight, source.ColorType, source.AlphaType);
        using (var canvas = new SKCanvas(result))
        using (var image = SKImage.FromBitmap(source))
        {
            // Concatenate onto the destination canvas the transform that maps the
            // stored (mis-oriented) pixels into upright destination space, then draw
            // the source at its native origin.
            canvas.Concat(OrientationMatrix(origin, source.Width, source.Height));
            canvas.DrawImage(image, 0, 0, Sampling, paint: null);
        }

        return result;
    }

    private static SKBitmap Copy(SKBitmap source)
    {
        var copy = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(copy);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0);
        return copy;
    }

    // The canonical EXIF-origin -> destination affine transforms. The source bitmap
    // (w x h) is drawn at its native origin; each matrix maps those source pixels into
    // the upright destination canvas (whose axes are swapped for the 5–8 origins).
    // Matrix values are column-vector form: (scaleX, skewX, transX / skewY, scaleY, transY).
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        // 2: mirror horizontal.
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        // 3: rotate 180.
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        // 4: mirror vertical.
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        // 5: transpose (mirror across main diagonal).
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        // 6: rotate 90 CW.
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        // 7: transverse (mirror across anti-diagonal).
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        // 8: rotate 90 CCW.
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.CreateIdentity(),
    };

    private static SKEncodedImageFormat ToSkFormat(ImageEncodeFormat format) => format switch
    {
        ImageEncodeFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        ImageEncodeFormat.Png => SKEncodedImageFormat.Png,
        ImageEncodeFormat.Webp => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Jpeg,
    };

    private static bool MatchesTargetFormat(SKData data, ImageEncodeFormat target)
    {
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return false;
        }

        return target switch
        {
            ImageEncodeFormat.Jpeg => codec.EncodedFormat == SKEncodedImageFormat.Jpeg,
            ImageEncodeFormat.Png => codec.EncodedFormat == SKEncodedImageFormat.Png,
            ImageEncodeFormat.Webp => codec.EncodedFormat == SKEncodedImageFormat.Webp,
            _ => false,
        };
    }
}
