#if ANDROID
using Android.Graphics;
using ZXing;
using ZXing.Common;
#endif

namespace Honua.Collect.App.Capture;

/// <summary>
/// Decodes a barcode / QR code from a still image (BACKLOG C6). The image is
/// captured with the camera or picked from the gallery, then decoded with the
/// pure-.NET ZXing core — ZXing.Net.Maui's live-preview control only targets
/// net6, so this still-image path keeps the feature on net10 without that
/// dependency.
/// </summary>
public static class BarcodeDecoder
{
    /// <summary>Decodes the first barcode found in the image, or null if none.</summary>
    /// <param name="imagePath">Path to the image file to decode.</param>
    /// <returns>The decoded payload text, or null.</returns>
    public static Task<string?> DecodeAsync(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

#if ANDROID
        return Task.Run(() =>
        {
            using var bitmap = BitmapFactory.DecodeFile(imagePath);
            if (bitmap is null)
            {
                return (string?)null;
            }

            var width = bitmap.Width;
            var height = bitmap.Height;
            var pixels = new int[width * height];
            bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);

            // Pack ARGB ints into the RGB24 byte buffer ZXing expects.
            var rgb = new byte[width * height * 3];
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                rgb[(i * 3) + 0] = (byte)((p >> 16) & 0xFF);
                rgb[(i * 3) + 1] = (byte)((p >> 8) & 0xFF);
                rgb[(i * 3) + 2] = (byte)(p & 0xFF);
            }

            var source = new RGBLuminanceSource(rgb, width, height, RGBLuminanceSource.BitmapFormat.RGB24);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions { TryHarder = true },
            };

            return reader.Decode(source)?.Text;
        });
#else
        // Other platforms can add a native decode path later.
        return Task.FromResult<string?>(null);
#endif
    }
}
