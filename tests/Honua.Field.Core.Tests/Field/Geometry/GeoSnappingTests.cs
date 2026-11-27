// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Tests.Field.Geometry;

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

    [Fact]
    public void Multi_target_snap_picks_the_closest_target()
    {
        // Two candidate features: a far one and a near one. The captured point is
        // ~2 m from the near feature's vertex and far from the other.
        var near = new SnapTarget([new(0.0, 0.0), new(0.0, 0.01)]);
        var far = new SnapTarget([new(0.5, 0.5), new(0.5, 0.51)]);
        var captured = new FieldGeoPoint(0.00002, 0.0);

        var result = GeoSnapping.Snap(captured, [far, near], toleranceMeters: 5);

        Assert.Equal(SnapKind.Vertex, result.Kind);
        Assert.Equal(0.0, result.Point.Latitude, 6);
        Assert.Equal(0.0, result.Point.Longitude, 6);
    }

    [Fact]
    public void Multi_target_snap_returns_none_when_all_targets_are_far()
    {
        var t1 = new SnapTarget([new(0.0, 0.0), new(0.0, 0.01)]);
        var t2 = new SnapTarget([new(1.0, 1.0), new(1.0, 1.01)]);
        var captured = new FieldGeoPoint(0.5, 0.5);

        var result = GeoSnapping.Snap(captured, [t1, t2], toleranceMeters: 5);

        Assert.Equal(SnapKind.None, result.Kind);
        Assert.Same(captured, result.Point);
    }

    [Fact]
    public void Uses_metre_tolerance_at_high_latitude_and_preserves_the_target_coordinates()
    {
        var captured = new FieldGeoPoint(80, 0);
        var vertex = new FieldGeoPoint(80, 0.0001, AccuracyMeters: 1);

        var miss = GeoSnapping.Snap(captured, [vertex], toleranceMeters: 1.9);
        var hit = GeoSnapping.Snap(captured, [vertex], toleranceMeters: 2);

        Assert.Equal(SnapKind.None, miss.Kind);
        Assert.Same(captured, miss.Point);
        Assert.Equal(SnapKind.Vertex, hit.Kind);
        Assert.Equal(new FieldGeoPoint(vertex.Latitude, vertex.Longitude), hit.Point);
        Assert.InRange(hit.DistanceMeters, 1.93, 1.94);
    }

    [Fact]
    public void Across_targets_the_first_equal_vertex_wins_and_vertices_beat_edges()
    {
        var captured = new FieldGeoPoint(0, 0);
        var firstVertex = new FieldGeoPoint(0.00002, 0);
        var secondVertex = new FieldGeoPoint(-0.00002, 0);
        var edge = new SnapTarget([new(0.00002, -0.001), new(0.00002, 0.001)]);

        var hit = GeoSnapping.Snap(captured, [edge, new SnapTarget([firstVertex]), new SnapTarget([secondVertex])], 5);

        Assert.Equal(SnapKind.Vertex, hit.Kind);
        Assert.Equal(firstVertex, hit.Point);
        Assert.Equal(2.2108, hit.DistanceMeters, 5);
    }

    [Fact]
    public void Equal_edges_within_a_target_keep_the_first_segment()
    {
        FieldGeoPoint[] path = [new(0.00002, -0.001), new(0.00002, 0.001), new(-0.00002, 0.001), new(-0.00002, -0.001)];

        var hit = GeoSnapping.Snap(new FieldGeoPoint(0, 0), path, 5);

        Assert.Equal(SnapKind.Edge, hit.Kind);
        Assert.Equal(0.00002, hit.Point.Latitude, 8);
        Assert.Equal(0, hit.Point.Longitude, 8);
    }

    [Fact]
    public void Antimeridian_segment_snaps_near_the_date_line_and_misses_near_greenwich()
    {
        FieldGeoPoint[] path = [new(0, 179.999), new(0, -179.999)];
        var distantCapture = new FieldGeoPoint(0.00002, 0);

        var hit = GeoSnapping.Snap(new FieldGeoPoint(0.00002, -179.9998), path, 5);
        var miss = GeoSnapping.Snap(distantCapture, path, 5);
        var vertex = GeoSnapping.Snap(new FieldGeoPoint(0, -179.99999), [new FieldGeoPoint(0, 180)], 5);

        Assert.Equal(SnapKind.Edge, hit.Kind);
        Assert.Equal(0, hit.Point.Latitude, 8);
        Assert.Equal(-179.9998, hit.Point.Longitude, 8);
        Assert.Equal(2.2108, hit.DistanceMeters, 5);
        Assert.Equal(SnapKind.None, miss.Kind);
        Assert.Same(distantCapture, miss.Point);
        Assert.Equal(SnapKind.Vertex, vertex.Kind);
        Assert.Equal(180, vertex.Point.Longitude);
        Assert.Equal(1.1132, vertex.DistanceMeters, 5);
    }
}
