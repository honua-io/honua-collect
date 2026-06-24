using SkiaSharp;

namespace Honua.Collect.Core.Tests.Imaging;

/// <summary>
/// Generates small synthetic image bytes for the C8 resize tests — no device, no
/// fixture files. Paints a deterministic two-tone pattern (left half red, right half
/// blue) so orientation can be asserted by sampling a known corner of the decoded
/// output. EXIF orientation is injected by hand-writing a minimal APP1/TIFF block.
/// </summary>
internal static class SyntheticImage
{
    /// <summary>Encodes a width x height image with the left half red and right half blue.</summary>
    public static byte[] TwoTone(int width, int height, SKEncodedImageFormat format, int quality = 90)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var bluePaint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(new SKRect(width / 2f, 0, width, height), bluePaint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>
    /// Wraps a JPEG with an EXIF APP1 segment carrying the given orientation tag (1–8),
    /// so a resizer's EXIF-normalisation path can be exercised. The pixel data is left
    /// unrotated; only the orientation tag is set.
    /// </summary>
    public static byte[] JpegWithExifOrientation(int width, int height, ushort orientation)
    {
        var jpeg = TwoTone(width, height, SKEncodedImageFormat.Jpeg);
        return InsertExifOrientation(jpeg, orientation);
    }

    // Inserts an APP1/Exif segment (little-endian TIFF, single Orientation IFD entry)
    // immediately after the SOI marker.
    private static byte[] InsertExifOrientation(byte[] jpeg, ushort orientation)
    {
        // TIFF header (little-endian) + 1 IFD entry (Orientation, tag 0x0112, SHORT) + next-IFD offset.
        var tiff = new List<byte>();
        tiff.AddRange("II"u8.ToArray());           // little-endian
        tiff.AddRange([0x2A, 0x00]);               // magic 42
        tiff.AddRange([0x08, 0x00, 0x00, 0x00]);   // offset to first IFD = 8
        tiff.AddRange([0x01, 0x00]);               // entry count = 1
        tiff.AddRange([0x12, 0x01]);               // tag 0x0112 (Orientation)
        tiff.AddRange([0x03, 0x00]);               // type SHORT
        tiff.AddRange([0x01, 0x00, 0x00, 0x00]);   // count = 1
        tiff.AddRange([(byte)(orientation & 0xFF), (byte)(orientation >> 8), 0x00, 0x00]); // value
        tiff.AddRange([0x00, 0x00, 0x00, 0x00]);   // next IFD offset = 0

        var payload = new List<byte>();
        payload.AddRange("Exif\0\0"u8.ToArray());
        payload.AddRange(tiff);

        var segmentLength = payload.Count + 2; // +2 for the length field itself
        var app1 = new List<byte>
        {
            0xFF, 0xE1,
            (byte)(segmentLength >> 8), (byte)(segmentLength & 0xFF),
        };
        app1.AddRange(payload);

        // SOI is the first two bytes (FF D8); splice APP1 right after it.
        var result = new List<byte>();
        result.AddRange(jpeg.AsSpan(0, 2).ToArray());
        result.AddRange(app1);
        result.AddRange(jpeg.AsSpan(2).ToArray());
        return result.ToArray();
    }

    /// <summary>Decodes bytes back to a bitmap for assertions.</summary>
    public static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes);

    /// <summary>The dominant colour sampled at the given pixel of decoded bytes.</summary>
    public static SKColor PixelAt(byte[] bytes, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        return bitmap.GetPixel(x, y);
    }
}
