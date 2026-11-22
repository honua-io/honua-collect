// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Imaging;

public readonly record struct ImageResizePlan(bool ResizeNeeded, int TargetWidth, int TargetHeight)
{
    public static ImageResizePlan For(int width, int height, int maxEdge)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEdge);

        var longest = Math.Max(width, height);
        if (longest <= maxEdge)
        {
            return new ImageResizePlan(false, width, height);
        }

        var scale = (double)maxEdge / longest;
        var targetWidth = ScaleEdge(width, scale, isLongest: width >= height, maxEdge);
        var targetHeight = ScaleEdge(height, scale, isLongest: height > width, maxEdge);
        return new ImageResizePlan(true, targetWidth, targetHeight);
    }

    private static int ScaleEdge(int edge, double scale, bool isLongest, int maxEdge)
    {
        // The strictly-longest edge maps to exactly maxEdge (no rounding drift);
        // the shorter edge scales proportionally, rounded and floored at 1 pixel.
        if (isLongest)
        {
            return maxEdge;
        }

        var scaled = (int)Math.Round(edge * scale, MidpointRounding.AwayFromZero);
        return Math.Max(1, scaled);
    }
}
