// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

public enum SnapKind
{
    None,

    Vertex,

    Edge,
}

public sealed record SnapResult(SnapKind Kind, FieldGeoPoint Point, double DistanceMeters);

public sealed record SnapTarget(IReadOnlyList<FieldGeoPoint> Vertices, bool Closed = false);

public static class GeoSnapping
{
    private const double MetersPerDegreeLat = 110_540.0;
    private const double MetersPerDegreeLonEquator = 111_320.0;

    public static SnapResult Snap(
        FieldGeoPoint point,
        IReadOnlyList<FieldGeoPoint> candidate,
        double toleranceMeters,
        bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(candidate);
        if (toleranceMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceMeters));
        }

        if (candidate.Count == 0)
        {
            return new SnapResult(SnapKind.None, point, double.PositiveInfinity);
        }

        var lat0Rad = point.Latitude * Math.PI / 180.0;
        var lonScale = MetersPerDegreeLonEquator * Math.Cos(lat0Rad);

        (double X, double Y) ToLocal(FieldGeoPoint p) =>
            ((p.Longitude - point.Longitude) * lonScale, (p.Latitude - point.Latitude) * MetersPerDegreeLat);

        FieldGeoPoint ToGeo(double x, double y) => new(
            point.Latitude + y / MetersPerDegreeLat,
            point.Longitude + x / lonScale);

        // Nearest vertex.
        var bestVertexDist = double.PositiveInfinity;
        (double X, double Y) bestVertex = default;
        foreach (var vertex in candidate)
        {
            var (vx, vy) = ToLocal(vertex);
            var d = Math.Sqrt((vx * vx) + (vy * vy));
            if (d < bestVertexDist)
            {
                bestVertexDist = d;
                bestVertex = (vx, vy);
            }
        }

        // Nearest point on any edge.
        var bestEdgeDist = double.PositiveInfinity;
        (double X, double Y) bestEdge = default;
        var segmentCount = closed ? candidate.Count : candidate.Count - 1;
        for (var i = 0; i < segmentCount; i++)
        {
            var a = ToLocal(candidate[i]);
            var b = ToLocal(candidate[(i + 1) % candidate.Count]);
            var (px, py, dist) = ClosestOnSegment(a, b);
            if (dist < bestEdgeDist)
            {
                bestEdgeDist = dist;
                bestEdge = (px, py);
            }
        }

        // Prefer a vertex when it is no farther than the edge (ties to vertex).
        if (bestVertexDist <= toleranceMeters && bestVertexDist <= bestEdgeDist + 1e-6)
        {
            return new SnapResult(SnapKind.Vertex, ToGeo(bestVertex.X, bestVertex.Y), bestVertexDist);
        }

        if (bestEdgeDist <= toleranceMeters)
        {
            return new SnapResult(SnapKind.Edge, ToGeo(bestEdge.X, bestEdge.Y), bestEdgeDist);
        }

        return new SnapResult(SnapKind.None, point, Math.Min(bestVertexDist, bestEdgeDist));
    }

    public static SnapResult Snap(
        FieldGeoPoint point,
        IEnumerable<SnapTarget> targets,
        double toleranceMeters)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(targets);

        var best = new SnapResult(SnapKind.None, point, double.PositiveInfinity);
        foreach (var target in targets)
        {
            if (target is null || target.Vertices.Count == 0)
            {
                continue;
            }

            var candidate = Snap(point, target.Vertices, toleranceMeters, target.Closed);

            // Keep the closest hit; on equal distance prefer a vertex snap.
            if (candidate.Kind != SnapKind.None &&
                (candidate.DistanceMeters < best.DistanceMeters - 1e-6 ||
                 (candidate.DistanceMeters <= best.DistanceMeters + 1e-6 &&
                  candidate.Kind == SnapKind.Vertex && best.Kind != SnapKind.Vertex)))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static (double X, double Y, double Distance) ClosestOnSegment((double X, double Y) a, (double X, double Y) b)
    {
        // Origin (0,0) is the captured point; find the closest point on a→b.
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lengthSq = (abx * abx) + (aby * aby);

        double t = 0;
        if (lengthSq > 0)
        {
            t = -((a.X * abx) + (a.Y * aby)) / lengthSq;
            t = Math.Clamp(t, 0, 1);
        }

        var px = a.X + (t * abx);
        var py = a.Y + (t * aby);
        return (px, py, Math.Sqrt((px * px) + (py * py)));
    }
}
