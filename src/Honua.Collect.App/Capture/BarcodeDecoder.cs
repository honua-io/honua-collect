// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

#if ANDROID
using Android.Graphics;
using Honua.Collect.Core.Imaging;
using ZXing;
using ZXing.Common;
#endif

namespace Honua.Collect.App.Capture;

public static class BarcodeDecoder
{
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
            var rgb = ArgbPixels.PackRgb(pixels);

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
