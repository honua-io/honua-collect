namespace Honua.Collect.Core.Imaging;

/// <summary>
/// The pure target-dimension decision for downscaling a captured photo before
/// it is stored and uploaded (BACKLOG C8). Given a source size and a longest-edge
/// cap, this computes whether a resize is needed and, if so, the aspect-preserving
/// target size — independent of any platform image library, so the maths is
/// unit-testable without a device. The app layer (<c>ImageCompressor</c>) applies
/// the plan with the MAUI Graphics image services.
/// </summary>
/// <param name="ResizeNeeded">Whether the source exceeds the cap and must be downscaled.</param>
/// <param name="TargetWidth">The downscaled width when <see cref="ResizeNeeded"/>; otherwise the source width.</param>
/// <param name="TargetHeight">The downscaled height when <see cref="ResizeNeeded"/>; otherwise the source height.</param>
public readonly record struct ImageResizePlan(bool ResizeNeeded, int TargetWidth, int TargetHeight)
{
    /// <summary>
    /// Computes the resize plan for a source image of <paramref name="width"/> x
    /// <paramref name="height"/> against a longest-edge cap of <paramref name="maxEdge"/>.
    /// </summary>
    /// <remarks>
    /// When the longest source edge is at or below <paramref name="maxEdge"/> the
    /// plan reports no resize and echoes the source size. Otherwise the longest edge
    /// is scaled down to exactly <paramref name="maxEdge"/> and the shorter edge is
    /// scaled by the same ratio, rounded to the nearest pixel and floored at 1 so a
    /// very long, thin image never collapses to a zero dimension. The aspect ratio is
    /// preserved within rounding.
    /// </remarks>
    /// <param name="width">Source width in pixels; must be positive.</param>
    /// <param name="height">Source height in pixels; must be positive.</param>
    /// <param name="maxEdge">Longest-edge cap in pixels; must be positive.</param>
    /// <returns>The plan describing whether and how to downscale.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If any argument is not positive.</exception>
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
