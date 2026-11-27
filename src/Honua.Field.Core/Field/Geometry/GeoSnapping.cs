// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;
using Honua.Sdk.Geometry;
using NetTopologySuite.Geometries;

namespace Honua.Field.Core.Field.Geometry;

/// <summary>What a captured point snapped to.</summary>
public enum SnapKind
{
    /// <summary>Nothing within tolerance; the original point is kept.</summary>
    None,

    /// <summary>Snapped to an existing vertex.</summary>
    Vertex,

    /// <summary>Snapped to the nearest point on an edge.</summary>
    Edge,
}

/// <summary>The outcome of a snap attempt.</summary>
/// <param name="Kind">What was snapped to.</param>
/// <param name="Point">The resulting point (snapped, or the original when <see cref="SnapKind.None"/>).</param>
/// <param name="DistanceMeters">Distance from the original point to the snap target.</param>
public sealed record SnapResult(SnapKind Kind, FieldGeoPoint Point, double DistanceMeters);

/// <summary>
/// A candidate feature to snap to: its ordered vertices and whether they form a
/// closed ring (polygon). Lines and points pass <paramref name="Closed"/> as
/// <see langword="false"/>.
/// </summary>
/// <param name="Vertices">Candidate geometry vertices, in order.</param>
/// <param name="Closed">Whether the candidate is a closed ring (polygon).</param>
public sealed record SnapTarget(IReadOnlyList<FieldGeoPoint> Vertices, bool Closed = false);

/// <summary>
/// Snaps a captured point to the vertices and edges of nearby features
/// (BACKLOG G7 — snap-to-feature / topology assist). This keeps adjacent
/// geometries coincident — shared boundaries, connected lines — which is what
/// makes field-captured GIS data topologically clean. Distances use a local
/// equirectangular projection in metres, accurate at snapping tolerances.
/// </summary>
public static class GeoSnapping
{
    // Field inputs are WGS 84 longitude/latitude. The SDK sees an unregistered
    // local Cartesian frame (SRID 0) in metres, centred on the captured point.
    private static readonly GeometryFactory LocalGeometry = new();
    private const double MetersPerDegreeLat = 110_540.0;
    private const double MetersPerDegreeLonEquator = 111_320.0;

    /// <summary>
    /// Snaps <paramref name="point"/> to the nearest vertex or edge of a candidate
    /// geometry within <paramref name="toleranceMeters"/>. Vertices win ties so
    /// captures prefer coincident nodes over mid-edge points.
    /// </summary>
    /// <param name="point">Captured point.</param>
    /// <param name="candidate">Candidate geometry vertices, in order.</param>
    /// <param name="toleranceMeters">Maximum snap distance.</param>
    /// <param name="closed">Whether the candidate is a closed ring (polygon).</param>
    /// <returns>The snap result.</returns>
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

        double LocalLatitude(FieldGeoPoint vertex) => (vertex.Latitude - point.Latitude) * MetersPerDegreeLat;

        var origin = LocalGeometry.CreatePoint(new Coordinate(0, 0));
        var vertices = new Coordinate[candidate.Count];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = candidate[i];
            vertices[i] = new Coordinate(WrapLongitude(vertex.Longitude - point.Longitude) * lonScale, LocalLatitude(vertex));
        }

        var nearestVertex = HonuaPlanarGeometryAnalyzer.FindNearestVertex(LocalGeometry.CreateMultiPointFromCoords(vertices), origin);

        var segmentCount = closed && candidate.Count > 1 ? candidate.Count : candidate.Count - 1;
        var segments = new LineString[segmentCount];
        for (var i = 0; i < segments.Length; i++)
        {
            var start = candidate[i];
            var end = candidate[(i + 1) % candidate.Count];
            var delta = WrapLongitude(end.Longitude - start.Longitude);
            // Unwrap each short segment as a unit to keep edges across the
            // antimeridian from crossing the capture origin on the far side.
            var midpoint = WrapLongitude(start.Longitude - point.Longitude) + delta / 2;
            var localStart = WrapLongitude(midpoint) - delta / 2;
            segments[i] = LocalGeometry.CreateLineString(
            [
                new Coordinate(localStart * lonScale, LocalLatitude(start)),
                new Coordinate((localStart + delta) * lonScale, LocalLatitude(end))
            ]);
        }

        var nearestEdge = segments.Length == 0
            ? null
            : HonuaPlanarGeometryAnalyzer.FindNearestPoints(LocalGeometry.CreateMultiLineString(segments), origin);
        var bestVertexDist = nearestVertex.Distance;
        var bestEdgeDist = nearestEdge?.Distance ?? double.PositiveInfinity;

        // Prefer a vertex when it is no farther than the edge (ties to vertex).
        if (bestVertexDist <= toleranceMeters && bestVertexDist <= bestEdgeDist + 1e-6)
        {
            var vertex = candidate[nearestVertex.CoordinateIndex];
            return new SnapResult(SnapKind.Vertex, new FieldGeoPoint(vertex.Latitude, vertex.Longitude), bestVertexDist);
        }

        if (nearestEdge is not null && bestEdgeDist <= toleranceMeters)
        {
            var edge = nearestEdge.FirstPoint;
            var snapped = new FieldGeoPoint(
                point.Latitude + edge.Y / MetersPerDegreeLat,
                WrapLongitude(point.Longitude + edge.X / lonScale));
            return new SnapResult(SnapKind.Edge, snapped, bestEdgeDist);
        }

        return new SnapResult(SnapKind.None, point, Math.Min(bestVertexDist, bestEdgeDist));
    }

    /// <summary>
    /// Snaps <paramref name="point"/> to the nearest vertex or edge across several
    /// candidate features within <paramref name="toleranceMeters"/>, returning the
    /// closest snap found. A vertex hit wins ties with an equally-close edge hit.
    /// </summary>
    /// <param name="point">Captured point.</param>
    /// <param name="targets">Candidate features to snap against.</param>
    /// <param name="toleranceMeters">Maximum snap distance.</param>
    /// <returns>The best snap result, or <see cref="SnapKind.None"/> if nothing is within tolerance.</returns>
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

    private static double WrapLongitude(double longitude) => Math.IEEERemainder(longitude, 360);
}
