using Honua.Collect.Core.Vision;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Vision;

public class GroundProjectionTests
{
    // Local-plane constants mirrored from GroundProjection / GeoSnapping.
    private const double MetersPerDegreeLat = 110_540.0;
    private const double MetersPerDegreeLonEquator = 111_320.0;

    [Fact]
    public void Center_point_projects_straight_along_the_bearing()
    {
        // Camera at the equator, looking due east (90°), target centred in frame.
        var pose = new CapturePose(new FieldGeoPoint(0.0, 0.0), BearingDegrees: 90.0, HorizontalFovDegrees: 60.0);

        var offset = GroundProjection.OffsetFor(pose, normalizedX: 0.5, groundRangeMeters: 100.0);

        Assert.Equal(100.0, offset.EastMeters, 6); // straight east
        Assert.Equal(0.0, offset.NorthMeters, 6);  // no northing
        Assert.Equal(100.0, offset.RangeMeters, 6);
    }

    [Fact]
    public void Right_edge_at_90deg_fov_subtends_45deg_from_axis()
    {
        // FOV 90° → half-FOV 45°, tan(45°)=1, right edge (x=1) → +45° from optical axis.
        // Optical axis points north (0°) → target bearing 45° (north-east).
        var pose = new CapturePose(new FieldGeoPoint(0.0, 0.0), BearingDegrees: 0.0, HorizontalFovDegrees: 90.0);

        var offset = GroundProjection.OffsetFor(pose, normalizedX: 1.0, groundRangeMeters: 100.0);

        var expected = 100.0 / Math.Sqrt(2.0);
        Assert.Equal(expected, offset.EastMeters, 6);
        Assert.Equal(expected, offset.NorthMeters, 6);
    }

    [Fact]
    public void Projects_image_point_to_expected_geographic_coordinate()
    {
        // Camera at equator/prime-meridian, due east, centred target, 111.32 m range.
        // 111.32 m east at the equator ≈ 0.001° longitude; latitude unchanged.
        var pose = new CapturePose(new FieldGeoPoint(0.0, 0.0), BearingDegrees: 90.0, HorizontalFovDegrees: 60.0);

        var point = GroundProjection.ProjectToGround(pose, normalizedX: 0.5, groundRangeMeters: 111.32);

        Assert.Equal(0.0, point.Latitude, 9);
        Assert.Equal(111.32 / MetersPerDegreeLonEquator, point.Longitude, 9);
    }

    [Fact]
    public void Longitude_offset_shrinks_with_latitude_cosine()
    {
        // Same easting at 60°N covers twice the longitude (cos 60° = 0.5).
        var pose = new CapturePose(new FieldGeoPoint(60.0, 10.0), BearingDegrees: 90.0, HorizontalFovDegrees: 60.0);

        var point = GroundProjection.ProjectToGround(pose, normalizedX: 0.5, groundRangeMeters: 100.0);

        var lonScale = MetersPerDegreeLonEquator * Math.Cos(60.0 * Math.PI / 180.0);
        Assert.Equal(60.0, point.Latitude, 9);
        Assert.Equal(10.0 + (100.0 / lonScale), point.Longitude, 9);
    }

    [Fact]
    public void Offset_to_geographic_and_back_round_trips_in_metres()
    {
        var camera = new FieldGeoPoint(47.6062, -122.3321);
        var offset = new GroundOffset(EastMeters: 250.0, NorthMeters: -130.0);

        var point = GroundProjection.ToGeographic(camera, offset);

        var lonScale = MetersPerDegreeLonEquator * Math.Cos(camera.Latitude * Math.PI / 180.0);
        var recoveredEast = (point.Longitude - camera.Longitude) * lonScale;
        var recoveredNorth = (point.Latitude - camera.Latitude) * MetersPerDegreeLat;
        Assert.Equal(250.0, recoveredEast, 6);
        Assert.Equal(-130.0, recoveredNorth, 6);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Rejects_out_of_range_normalized_x(double x)
    {
        var pose = new CapturePose(new FieldGeoPoint(0, 0), 0, 60);
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundProjection.OffsetFor(pose, x, 100));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(180.0)]
    [InlineData(-5.0)]
    public void Rejects_out_of_range_fov(double fov)
    {
        var pose = new CapturePose(new FieldGeoPoint(0, 0), 0, fov);
        Assert.Throws<ArgumentOutOfRangeException>(() => pose.Validated());
    }
}
