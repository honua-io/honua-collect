using Honua.Collect.Core.Imaging;
using SkiaSharp;

namespace Honua.Collect.Core.Tests.Imaging;

public class SkiaImageResizerTests
{
    private readonly SkiaImageResizer _resizer = new();

    [Fact]
    public void Downscales_over_budget_image_to_cap_preserving_aspect()
    {
        var source = SyntheticImage.TwoTone(4000, 3000, SKEncodedImageFormat.Jpeg);

        var result = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 1600 });

        Assert.True(result.Resized);
        Assert.True(result.ReEncoded);
        Assert.Equal(1600, result.Width);
        Assert.Equal(1200, result.Height); // 4:3 preserved
        // The decoded output really is 1600x1200.
        using var decoded = SyntheticImage.Decode(result.Bytes);
        Assert.Equal(1600, decoded.Width);
        Assert.Equal(1200, decoded.Height);
    }

    [Fact]
    public void Downscale_shrinks_the_byte_payload()
    {
        var source = SyntheticImage.TwoTone(4000, 3000, SKEncodedImageFormat.Jpeg);

        var result = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 800, Quality = 60 });

        Assert.True(result.ByteLength < source.Length);
    }

    [Fact]
    public void Within_budget_same_format_is_a_verbatim_no_op()
    {
        var source = SyntheticImage.TwoTone(800, 600, SKEncodedImageFormat.Jpeg);

        var result = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 1600, Format = ImageEncodeFormat.Jpeg });

        Assert.False(result.Resized);
        Assert.False(result.ReEncoded);
        Assert.Same(source, result.Bytes); // byte-for-byte identical reference
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void Within_budget_re_encodes_when_skip_disabled()
    {
        var source = SyntheticImage.TwoTone(800, 600, SKEncodedImageFormat.Jpeg);

        var result = _resizer.Resize(
            source, new ImageResizeOptions { MaxEdge = 1600, SkipWhenWithinBudget = false });

        Assert.False(result.Resized);
        Assert.True(result.ReEncoded);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void Within_budget_but_format_change_re_encodes()
    {
        var source = SyntheticImage.TwoTone(800, 600, SKEncodedImageFormat.Png);

        var result = _resizer.Resize(
            source, new ImageResizeOptions { MaxEdge = 1600, Format = ImageEncodeFormat.Jpeg });

        // Not resized, but a PNG->JPEG transcode still re-encodes.
        Assert.False(result.Resized);
        Assert.True(result.ReEncoded);
        Assert.Equal(ImageEncodeFormat.Jpeg, result.Format);
        Assert.NotSame(source, result.Bytes);
    }

    [Theory]
    [InlineData(ImageEncodeFormat.Jpeg)]
    [InlineData(ImageEncodeFormat.Png)]
    [InlineData(ImageEncodeFormat.Webp)]
    public void Honours_target_format(ImageEncodeFormat format)
    {
        var source = SyntheticImage.TwoTone(2000, 2000, SKEncodedImageFormat.Jpeg);

        var result = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 500, Format = format });

        Assert.Equal(format, result.Format);
        using var codec = SKCodec.Create(new MemoryStream(result.Bytes));
        var expected = format switch
        {
            ImageEncodeFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            ImageEncodeFormat.Png => SKEncodedImageFormat.Png,
            ImageEncodeFormat.Webp => SKEncodedImageFormat.Webp,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        Assert.Equal(expected, codec.EncodedFormat);
    }

    [Fact]
    public void Lower_quality_yields_smaller_jpeg()
    {
        var source = SyntheticImage.TwoTone(2000, 1500, SKEncodedImageFormat.Png);

        var high = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 1000, Quality = 95 });
        var low = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 1000, Quality = 30 });

        Assert.True(low.ByteLength < high.ByteLength);
    }

    [Fact]
    public void Exif_orientation_6_is_normalised_to_upright_dimensions()
    {
        // Orientation 6 (rotate 90 CW): a stored 1000x600 frame is upright at 600x1000.
        var source = SyntheticImage.JpegWithExifOrientation(1000, 600, orientation: 6);

        var result = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 4000 });

        // Even though no downscale was needed, the EXIF rewrite forces a re-encode and
        // the output dimensions are swapped to the upright orientation.
        Assert.True(result.ReEncoded);
        Assert.Equal(600, result.Width);
        Assert.Equal(1000, result.Height);
    }

    [Fact]
    public void Exif_orientation_6_moves_the_red_region_upright()
    {
        // Stored frame: left half red, right half blue. Orientation 6 = rotate 90 CW,
        // which sends the original LEFT edge to the TOP. So the upright image's top row
        // should read red and its bottom row blue.
        var source = SyntheticImage.JpegWithExifOrientation(1000, 600, orientation: 6);

        var result = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 4000 });

        var top = SyntheticImage.PixelAt(result.Bytes, result.Width / 2, 5);
        var bottom = SyntheticImage.PixelAt(result.Bytes, result.Width / 2, result.Height - 5);
        Assert.True(top.Red > top.Blue, $"top should be reddish but was {top}");
        Assert.True(bottom.Blue > bottom.Red, $"bottom should be blueish but was {bottom}");
    }

    [Fact]
    public void Orientation_1_within_budget_is_a_no_op()
    {
        var source = SyntheticImage.JpegWithExifOrientation(800, 600, orientation: 1);

        var result = _resizer.Resize(source, new ImageResizeOptions { MaxEdge = 1600 });

        Assert.False(result.ReEncoded);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void Empty_bytes_throw()
        => Assert.Throws<ArgumentException>(() => _resizer.Resize([], new ImageResizeOptions()));

    [Fact]
    public void Non_image_bytes_throw_decode_exception()
        => Assert.Throws<ImageDecodeException>(
            () => _resizer.Resize([1, 2, 3, 4, 5, 6, 7, 8], new ImageResizeOptions()));

    [Fact]
    public void Invalid_quality_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => _resizer.Resize(
                SyntheticImage.TwoTone(10, 10, SKEncodedImageFormat.Jpeg),
                new ImageResizeOptions { Quality = 0 }));
}
