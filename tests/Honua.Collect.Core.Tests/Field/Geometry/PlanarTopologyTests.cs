using Honua.Collect.Core.Field.Geometry;

namespace Honua.Collect.Core.Tests.Field.Geometry;

public class PlanarTopologyTests
{
    // A projected line (e.g. UTM/State-Plane metres) far from the equator, where a
    // WGS84-only snapper's equirectangular approximation would distort distances.
    // Working directly in the layer CRS keeps the metres honest.
    private static readonly PlanarFeature ProjectedLine = new(
    [
        new PlanarPoint(500_000, 6_000_000),
        new PlanarPoint(500_100, 6_000_000), // 100 m east
    ]);

    [Fact]
    public void Snaps_to_a_nearby_vertex_within_tolerance()
    {
        // Off the western END of the segment, so the first vertex — not the edge
        // foot, which clamps to that same vertex — is the nearest target.
        var captured = new PlanarPoint(500_000 - 3, 6_000_000 + 2); // ~3.6 m from the first vertex
        var result = PlanarTopology.Snap(captured, ProjectedLine, tolerance: 5);

        Assert.Equal(PlanarSnapKind.Vertex, result.Kind);
        Assert.Equal(new PlanarPoint(500_000, 6_000_000), result.Point);
        Assert.Equal(0, result.VertexIndex);
        Assert.True(result.Distance <= 5);
    }

    [Fact]
    public void Does_not_snap_when_outside_tolerance()
    {
        var captured = new PlanarPoint(500_050, 6_000_050); // 50 m north of the segment middle
        var result = PlanarTopology.Snap(captured, ProjectedLine, tolerance: 5);

        Assert.Equal(PlanarSnapKind.None, result.Kind);
        Assert.Equal(captured, result.Point);
        Assert.Equal(50, result.Distance, 6);
    }

    [Fact]
    public void Edge_snap_lands_on_the_foot_of_the_perpendicular()
    {
        var captured = new PlanarPoint(500_050, 6_000_004); // above the segment middle
        var result = PlanarTopology.Snap(captured, ProjectedLine, tolerance: 5);

        Assert.Equal(PlanarSnapKind.Edge, result.Kind);
        // Foot of perpendicular is straight down onto the horizontal segment.
        Assert.Equal(500_050, result.Point.X, 6);
        Assert.Equal(6_000_000, result.Point.Y, 6);
        Assert.Equal(4, result.Distance, 6);
        Assert.Equal(0, result.SegmentIndex);
    }

    [Fact]
    public void Vertex_wins_a_tie_with_an_equally_close_edge()
    {
        // Directly above the first vertex: vertex and edge are the same distance.
        var captured = new PlanarPoint(500_000, 6_000_000 + 3);
        var result = PlanarTopology.Snap(captured, ProjectedLine, tolerance: 5);

        Assert.Equal(PlanarSnapKind.Vertex, result.Kind);
    }

    [Fact]
    public void Snaps_to_the_nearest_of_many_features()
    {
        var near = new PlanarFeature([new PlanarPoint(500_010, 6_000_010)]);
        var far = new PlanarFeature([new PlanarPoint(500_900, 6_000_900)]);
        var captured = new PlanarPoint(500_012, 6_000_010);

        var result = PlanarTopology.Snap(captured, new[] { far, near }, tolerance: 5);

        Assert.Equal(PlanarSnapKind.Vertex, result.Kind);
        Assert.Equal(new PlanarPoint(500_010, 6_000_010), result.Point);
        Assert.Equal(1, result.FeatureIndex); // the "near" feature
    }

    [Fact]
    public void Closed_ring_snaps_to_its_closing_segment()
    {
        var ring = new PlanarFeature(
        [
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 0),
            new PlanarPoint(10, 10),
            new PlanarPoint(0, 10),
        ],
            Closed: true);

        // Point just outside the left (closing) edge between (0,10) and (0,0).
        var captured = new PlanarPoint(-1, 5);
        var result = PlanarTopology.Snap(captured, ring, tolerance: 2);

        Assert.Equal(PlanarSnapKind.Edge, result.Kind);
        Assert.Equal(0, result.Point.X, 6);
        Assert.Equal(5, result.Point.Y, 6);
    }

    [Fact]
    public void Insert_shared_vertex_splits_the_coincident_edge()
    {
        var feature = new PlanarFeature(
        [
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 0),
        ]);

        var result = PlanarTopology.InsertSharedVertex(new PlanarPoint(5, 0.5), feature, tolerance: 1);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
        Assert.Equal(new PlanarPoint(5, 0), result[1]); // foot inserted between the two ends
    }

    [Fact]
    public void Insert_shared_vertex_returns_null_when_the_point_is_a_vertex_hit()
    {
        var feature = new PlanarFeature([new PlanarPoint(0, 0), new PlanarPoint(10, 0)]);

        // Off the segment's end, so the existing (0,0) vertex — not an edge
        // interior — is the nearest target; no split should occur.
        var result = PlanarTopology.InsertSharedVertex(new PlanarPoint(-0.3, 0.2), feature, tolerance: 1);

        Assert.Null(result);
    }

    [Fact]
    public void Insert_shared_vertex_returns_null_when_outside_tolerance()
    {
        var feature = new PlanarFeature([new PlanarPoint(0, 0), new PlanarPoint(10, 0)]);
        var result = PlanarTopology.InsertSharedVertex(new PlanarPoint(5, 50), feature, tolerance: 1);
        Assert.Null(result);
    }

    [Fact]
    public void Close_ring_snaps_the_open_end_to_the_first_vertex()
    {
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 0),
            new PlanarPoint(10, 10),
            new PlanarPoint(0.3, 0.3), // ~0.42 from the start
        };

        var ring = PlanarTopology.CloseRing(vertices, tolerance: 1, out var closed);

        Assert.True(closed);
        Assert.Equal(vertices[0], ring[^1]); // last replaced with an exact copy of the first
    }

    [Fact]
    public void Close_ring_leaves_a_too_wide_gap_open()
    {
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 0),
            new PlanarPoint(10, 10),
            new PlanarPoint(5, 5), // 7+ from the start
        };

        var ring = PlanarTopology.CloseRing(vertices, tolerance: 1, out var closed);

        Assert.False(closed);
        Assert.Same(vertices, ring);
    }

    [Fact]
    public void Detects_a_self_intersecting_polyline()
    {
        // A bow-tie / figure-of-eight crossing.
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 10),
            new PlanarPoint(0, 10),
            new PlanarPoint(10, 0),
        };

        Assert.True(PlanarTopology.HasSelfIntersection(vertices));
    }

    [Fact]
    public void Simple_polyline_has_no_self_intersection()
    {
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 0),
            new PlanarPoint(10, 10),
        };

        Assert.False(PlanarTopology.HasSelfIntersection(vertices));
    }

    [Fact]
    public void Simple_closed_square_ring_is_not_self_intersecting()
    {
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 0),
            new PlanarPoint(10, 10),
            new PlanarPoint(0, 10),
        };

        // The closing segment back to the start must not count as an intersection.
        Assert.False(PlanarTopology.HasSelfIntersection(vertices, closed: true));
    }

    [Fact]
    public void Detects_a_self_intersecting_ring()
    {
        // A non-simple ring (edges cross before closing).
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 10),
            new PlanarPoint(10, 0),
            new PlanarPoint(0, 10),
        };

        Assert.True(PlanarTopology.HasSelfIntersection(vertices, closed: true));
    }

    [Fact]
    public void Flags_a_segment_shorter_than_the_minimum()
    {
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(0.5, 0), // 0.5 m sliver
            new PlanarPoint(10, 0),
        };

        Assert.Equal(0, PlanarTopology.FirstSegmentShorterThan(vertices, minLength: 1));
        Assert.False(PlanarTopology.MeetsMinimumSegmentLength(vertices, minLength: 1));
    }

    [Fact]
    public void Accepts_geometry_whose_segments_all_meet_the_minimum()
    {
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(5, 0),
            new PlanarPoint(10, 0),
        };

        Assert.Equal(-1, PlanarTopology.FirstSegmentShorterThan(vertices, minLength: 1));
        Assert.True(PlanarTopology.MeetsMinimumSegmentLength(vertices, minLength: 1));
    }

    [Fact]
    public void Minimum_segment_length_checks_the_closing_segment_of_a_ring()
    {
        // The closing segment from the last vertex back to the first is the sliver.
        var vertices = new[]
        {
            new PlanarPoint(0, 0),
            new PlanarPoint(10, 0),
            new PlanarPoint(10, 10),
            new PlanarPoint(0.2, 0),
        };

        Assert.Equal(3, PlanarTopology.FirstSegmentShorterThan(vertices, minLength: 1, closed: true));
    }

    [Fact]
    public void Tolerance_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlanarTopology.Snap(new PlanarPoint(0, 0), ProjectedLine, tolerance: 0));
    }

    [Fact]
    public void Works_in_layer_crs_without_funnelling_through_wgs84()
    {
        // Coordinates well outside any valid lat/lon range: only a CRS-neutral
        // planar snapper can accept them. A WGS84-only type would clamp or reject.
        var feature = new PlanarFeature([new PlanarPoint(2_600_000, 1_200_000)]); // Swiss LV95-style
        var captured = new PlanarPoint(2_600_001, 1_200_001);

        var result = PlanarTopology.Snap(captured, feature, tolerance: 5);

        Assert.Equal(PlanarSnapKind.Vertex, result.Kind);
        Assert.Equal(new PlanarPoint(2_600_000, 1_200_000), result.Point);
    }
}
