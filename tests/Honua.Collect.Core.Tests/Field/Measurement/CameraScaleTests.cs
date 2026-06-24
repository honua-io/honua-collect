using Honua.Collect.Core.Field.Measurement;

namespace Honua.Collect.Core.Tests.Field.Measurement;

public class CameraScaleTests
{
    [Fact]
    public void Meters_per_pixel_is_distance_over_focal_length_pixels()
    {
        // f = 1000 px, subject 10 m away → 0.01 m/px.
        var scale = CameraScale.FromFocalLengthPixels(focalLengthPixels: 1000, distanceMeters: 10);
        Assert.Equal(0.01, scale.MetersPerPixel, 9);
    }

    [Fact]
    public void Converts_a_pixel_length_to_metres_by_similar_triangles()
    {
        // f = 1000 px, distance 10 m: a 200 px subject is 200 * (10/1000) = 2.0 m.
        var scale = CameraScale.FromFocalLengthPixels(1000, 10);
        Assert.Equal(2.0, scale.LengthMeters(200), 9);
        Assert.Equal(0.0, scale.LengthMeters(0), 9);
    }

    [Fact]
    public void Recovers_focal_length_in_pixels_from_sensor_geometry()
    {
        // f = 4 mm, sensor width 6 mm, image 6000 px → f_px = 4 * 6000 / 6 = 4000 px.
        // At 5 m a 400 px feature is 400 * (5/4000) = 0.5 m.
        var scale = CameraScale.FromSensor(
            focalLengthMillimeters: 4,
            sensorWidthMillimeters: 6,
            imageWidthPixels: 6000,
            distanceMeters: 5);

        Assert.Equal(4000, scale.FocalLengthPixels, 6);
        Assert.Equal(0.5, scale.LengthMeters(400), 9);
    }

    [Fact]
    public void Measures_distance_between_two_image_points()
    {
        // f = 1000 px, distance 10 m, 3-4-5 → 500 px * 0.01 = 5.0 m.
        var scale = CameraScale.FromFocalLengthPixels(1000, 10);
        Assert.Equal(5.0, scale.DistanceBetween(new PixelPoint(0, 0), new PixelPoint(300, 400)), 9);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1000, 0)]
    [InlineData(-1, 10)]
    [InlineData(double.NaN, 10)]
    [InlineData(1000, double.PositiveInfinity)]
    public void FromFocalLengthPixels_rejects_non_positive_or_non_finite(double focal, double distance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraScale.FromFocalLengthPixels(focal, distance));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Length_rejects_negative_or_nan(double bad)
    {
        var scale = CameraScale.FromFocalLengthPixels(1000, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.LengthMeters(bad));
    }
}
