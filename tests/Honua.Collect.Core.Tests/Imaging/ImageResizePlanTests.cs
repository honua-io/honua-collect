using Honua.Collect.Core.Imaging;

namespace Honua.Collect.Core.Tests.Imaging;

public class ImageResizePlanTests
{
    [Fact]
    public void Within_cap_does_not_resize_and_echoes_source()
    {
        var plan = ImageResizePlan.For(800, 600, 1600);

        Assert.False(plan.ResizeNeeded);
        Assert.Equal(800, plan.TargetWidth);
        Assert.Equal(600, plan.TargetHeight);
    }

    [Fact]
    public void Longest_edge_exactly_at_cap_does_not_resize()
    {
        var plan = ImageResizePlan.For(1600, 900, 1600);

        Assert.False(plan.ResizeNeeded);
        Assert.Equal(1600, plan.TargetWidth);
        Assert.Equal(900, plan.TargetHeight);
    }

    [Fact]
    public void Landscape_over_cap_scales_width_to_cap_and_preserves_aspect()
    {
        // 4000x3000 (4:3) capped at 1600 -> 1600x1200
        var plan = ImageResizePlan.For(4000, 3000, 1600);

        Assert.True(plan.ResizeNeeded);
        Assert.Equal(1600, plan.TargetWidth);
        Assert.Equal(1200, plan.TargetHeight);
    }

    [Fact]
    public void Portrait_over_cap_scales_height_to_cap_and_preserves_aspect()
    {
        // 3000x4000 capped at 1600 -> 1200x1600
        var plan = ImageResizePlan.For(3000, 4000, 1600);

        Assert.True(plan.ResizeNeeded);
        Assert.Equal(1200, plan.TargetWidth);
        Assert.Equal(1600, plan.TargetHeight);
    }

    [Fact]
    public void Square_over_cap_maps_both_edges_to_cap()
    {
        var plan = ImageResizePlan.For(2048, 2048, 1024);

        Assert.True(plan.ResizeNeeded);
        Assert.Equal(1024, plan.TargetWidth);
        Assert.Equal(1024, plan.TargetHeight);
    }

    [Fact]
    public void One_pixel_over_cap_resizes()
    {
        var plan = ImageResizePlan.For(1601, 1000, 1600);

        Assert.True(plan.ResizeNeeded);
        Assert.Equal(1600, plan.TargetWidth);
        // 1000 * 1600/1601 = 999.375 -> rounds to 999
        Assert.Equal(999, plan.TargetHeight);
    }

    [Fact]
    public void Extreme_aspect_floors_short_edge_at_one_pixel()
    {
        // A very long, thin strip must never collapse the short edge to zero.
        var plan = ImageResizePlan.For(10000, 3, 1600);

        Assert.True(plan.ResizeNeeded);
        Assert.Equal(1600, plan.TargetWidth);
        Assert.Equal(1, plan.TargetHeight);
    }

    [Fact]
    public void Rounding_uses_nearest_away_from_zero()
    {
        // 100x75 capped at 66: scale 0.66 -> height 75*0.66=49.5 -> 50
        var plan = ImageResizePlan.For(100, 75, 66);

        Assert.True(plan.ResizeNeeded);
        Assert.Equal(66, plan.TargetWidth);
        Assert.Equal(50, plan.TargetHeight);
    }

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(100, 0, 100)]
    [InlineData(100, 100, 0)]
    [InlineData(-1, 100, 100)]
    public void Non_positive_arguments_throw(int width, int height, int maxEdge)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ImageResizePlan.For(width, height, maxEdge));
}
