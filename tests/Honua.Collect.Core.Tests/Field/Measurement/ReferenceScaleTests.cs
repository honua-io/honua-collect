using Honua.Collect.Core.Field.Measurement;

namespace Honua.Collect.Core.Tests.Field.Measurement;

public class ReferenceScaleTests
{
    // A 1.0 m reference spans 200 px → 0.005 m/px.
    private static ReferenceScale Scale() => ReferenceScale.FromReference(1.0, 200);

    [Fact]
    public void Derives_meters_per_pixel_from_a_reference()
    {
        Assert.Equal(0.005, Scale().MetersPerPixel, 6);
    }

    [Fact]
    public void Converts_pixel_lengths_to_metres()
    {
        var scale = Scale();
        Assert.Equal(2.0, scale.LengthMeters(400), 6); // 400 px * 0.005
        Assert.Equal(0.0, scale.LengthMeters(0), 6);
    }

    [Fact]
    public void Converts_pixel_areas_with_the_scale_squared()
    {
        // 200x200 px = 40000 px² → (200*0.005)² = 1.0 m²
        Assert.Equal(1.0, Scale().AreaSquareMeters(40000), 6);
    }

    [Fact]
    public void Measures_distance_between_two_image_points()
    {
        // 3-4-5 triangle: 300/400/500 px → 500 px * 0.005 = 2.5 m
        var distance = Scale().DistanceMeters(new PixelPoint(0, 0), new PixelPoint(300, 400));
        Assert.Equal(2.5, distance, 6);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(1.0, 0)]
    [InlineData(-1, 200)]
    [InlineData(double.NaN, 200)]
    [InlineData(1.0, double.PositiveInfinity)]
    public void FromReference_rejects_non_positive_or_non_finite_inputs(double real, double pixels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceScale.FromReference(real, pixels));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Length_and_area_reject_negative_or_nan(double bad)
    {
        var scale = Scale();
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.LengthMeters(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.AreaSquareMeters(bad));
    }
}
