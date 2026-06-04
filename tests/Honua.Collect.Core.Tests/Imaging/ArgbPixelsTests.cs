using Honua.Collect.Core.Imaging;

namespace Honua.Collect.Core.Tests.Imaging;

public class ArgbPixelsTests
{
    [Fact]
    public void PackRgb_drops_alpha_and_orders_r_g_b()
    {
        // 0xAARRGGBB -> [R, G, B]
        var packed = ArgbPixels.PackRgb([unchecked((int)0xFF1A2B3C)]);

        Assert.Equal(new byte[] { 0x1A, 0x2B, 0x3C }, packed);
    }

    [Fact]
    public void PackRgb_handles_multiple_pixels_contiguously()
    {
        var pixels = new[]
        {
            unchecked((int)0xFFFF0000), // red
            unchecked((int)0xFF00FF00), // green
            unchecked((int)0xFF0000FF), // blue
        };

        var packed = ArgbPixels.PackRgb(pixels);

        Assert.Equal(
            new byte[] { 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF },
            packed);
    }

    [Fact]
    public void PackRgb_ignores_alpha_byte_regardless_of_value()
    {
        // Alpha 0x00 vs 0xFF must not change the RGB output.
        var opaque = ArgbPixels.PackRgb([unchecked((int)0xFF112233)]);
        var transparent = ArgbPixels.PackRgb([0x00112233]);

        Assert.Equal(opaque, transparent);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, transparent);
    }

    [Fact]
    public void PackRgb_empty_returns_empty()
        => Assert.Empty(ArgbPixels.PackRgb([]));

    [Fact]
    public void PackRgb_output_length_is_three_per_pixel()
        => Assert.Equal(12, ArgbPixels.PackRgb(new int[4]).Length);

    [Fact]
    public void PackRgb_null_throws()
        => Assert.Throws<ArgumentNullException>(() => ArgbPixels.PackRgb(null!));
}
