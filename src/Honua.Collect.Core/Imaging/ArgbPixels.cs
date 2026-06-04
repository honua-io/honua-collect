namespace Honua.Collect.Core.Imaging;

/// <summary>
/// Pure pixel-buffer conversions for the still-image barcode/QR decode path
/// (BACKLOG C6). The Android bitmap exposes pixels as packed ARGB
/// <see cref="int"/>s, while the ZXing core wants a tightly-packed RGB24
/// <see cref="byte"/> buffer; this packing has no platform dependency, so it
/// lives in Core and is unit-testable with known pixels. The app's Android
/// decode path calls <see cref="PackRgb"/> after reading the bitmap pixels.
/// </summary>
public static class ArgbPixels
{
    /// <summary>
    /// Packs an array of <c>0xAARRGGBB</c> pixels into a contiguous RGB24 buffer
    /// (3 bytes per pixel, R then G then B), dropping the alpha channel — the
    /// layout <c>ZXing</c>'s <c>RGBLuminanceSource</c> expects for
    /// <c>BitmapFormat.RGB24</c>.
    /// </summary>
    /// <param name="argbPixels">Packed ARGB pixels, one int per pixel.</param>
    /// <returns>An RGB24 byte buffer of length <c>argbPixels.Length * 3</c>.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="argbPixels"/> is null.</exception>
    public static byte[] PackRgb(int[] argbPixels)
    {
        ArgumentNullException.ThrowIfNull(argbPixels);

        var rgb = new byte[argbPixels.Length * 3];
        for (var i = 0; i < argbPixels.Length; i++)
        {
            var p = argbPixels[i];
            rgb[(i * 3) + 0] = (byte)((p >> 16) & 0xFF); // R
            rgb[(i * 3) + 1] = (byte)((p >> 8) & 0xFF);  // G
            rgb[(i * 3) + 2] = (byte)(p & 0xFF);         // B
        }

        return rgb;
    }
}
