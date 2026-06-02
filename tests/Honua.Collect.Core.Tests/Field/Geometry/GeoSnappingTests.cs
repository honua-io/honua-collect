using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Geometry;

public class GeoSnappingTests
{
    // A short line near the equator where 1 degree lon ~ 111 km.
    private static readonly FieldGeoPoint[] Line =
    [
        new(0.0, 0.0),
        new(0.0, 0.01), // ~1.11 km east
    ];

    [Fact]
    public void Snaps_to_a_nearby_vertex_within_tolerance()
    {
        // ~2 m north of the first vertex.
        var captured = new FieldGeoPoint(0.00002, 0.0);
        var result = GeoSnapping.Snap(captured, Line, toleranceMeters: 5);

        Assert.Equal(SnapKind.Vertex, result.Kind);
        Assert.Equal(0.0, result.Point.Latitude, 6);
        Assert.Equal(0.0, result.Point.Longitude, 6);
    }

    [Fact]
    public void Snaps_to_an_edge_when_no_vertex_is_close()
    {
        // Near the middle of the segment, a few metres north of it.
        var captured = new FieldGeoPoint(0.00002, 0.005);
        var result = GeoSnapping.Snap(captured, Line, toleranceMeters: 5);

        Assert.Equal(SnapKind.Edge, result.Kind);
        Assert.Equal(0.0, result.Point.Latitude, 5);       // snapped onto the line (lat 0)
        Assert.Equal(0.005, result.Point.Longitude, 5);    // kept its position along the line
    }

    [Fact]
    public void Returns_none_when_outside_tolerance()
    {
        var captured = new FieldGeoPoint(0.01, 0.005); // ~1.1 km off the line
        var result = GeoSnapping.Snap(captured, Line, toleranceMeters: 5);

        Assert.Equal(SnapKind.None, result.Kind);
        Assert.Same(captured, result.Point);
        Assert.True(result.DistanceMeters > 5);
    }

    [Fact]
    public void Closed_ring_snaps_to_the_closing_segment()
    {
        // Square ring; the closing edge (last vertex -> first) runs along lon 0.
        FieldGeoPoint[] ring = [new(0, 0), new(0, 0.01), new(0.01, 0.01), new(0.01, 0)];
        // ~2 m east of the middle of the closing edge.
        var captured = new FieldGeoPoint(0.005, 0.00002);
        var result = GeoSnapping.Snap(captured, ring, toleranceMeters: 5, closed: true);

        Assert.Equal(SnapKind.Edge, result.Kind);
        Assert.Equal(0.0, result.Point.Longitude, 5);   // snapped onto the closing edge
        Assert.Equal(0.005, result.Point.Latitude, 5);  // kept its position along it
    }
}
