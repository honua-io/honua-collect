// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Imaging;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;

namespace Honua.Collect.App.Capture;

public static class ImageCompressor
{
    public const int DefaultMaxEdge = 1600;

    public const float DefaultQuality = 0.75f;

    public static async Task<string> CompressAsync(
        string sourcePath, int maxEdge = DefaultMaxEdge, float quality = DefaultQuality)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        using var input = File.OpenRead(sourcePath);
        // PlatformImage.FromStream and Downsize both wrap native bitmaps (IImage is
        // IDisposable); leaving them to the GC finalizer leaks native memory on every
        // capture on the offline hot path. Dispose the decoded image, and dispose the
        // downsized intermediate separately — guarding against double-dispose when no
        // resize was needed and output is the same instance as image.
        using var image = PlatformImage.FromStream(input);

        var plan = ImageResizePlan.For((int)image.Width, (int)image.Height, maxEdge);
        var resized = plan.ResizeNeeded ? image.Downsize(maxEdge) : null;
        var output = resized ?? image;

        try
        {
            var destination = CaptureFiles.NewPath(".jpg");
            using var stream = File.Create(destination);
            await output.SaveAsync(stream, ImageFormat.Jpeg, quality);
            return destination;
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
