using System.Text.Json;
using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Geometry;

public class GeometryCaptureTests
{
    [Fact]
    public void Point_capture_keeps_a_single_vertex_and_sets_record_location()
    {
        var session = new GeometryCaptureSession(CapturedGeometryType.Point);
        session.AddVertex(new FieldGeoPoint(1, 1));
        session.AddVertex(new FieldGeoPoint(45.5, -122.6)); // replaces

        Assert.Equal(1, session.Count);
        Assert.True(session.IsComplete);

        var record = new FieldRecord { RecordId = "r", FormId = "f" };
        session.ApplyTo(record, "geom");

        Assert.Equal(45.5, record.Location!.Latitude);
        Assert.Equal(-122.6, record.Location.Longitude);
    }

    [Fact]
    public void Line_needs_two_vertices_and_supports_undo_and_move()
    {
        var session = new GeometryCaptureSession(CapturedGeometryType.Line);
        session.AddVertex(new FieldGeoPoint(0, 0));
        Assert.False(session.IsComplete);

        session.AddVertex(new FieldGeoPoint(0, 1));
        session.AddVertex(new FieldGeoPoint(0, 2));
        Assert.True(session.Undo());
        Assert.Equal(2, session.Count);

        session.MoveVertex(1, new FieldGeoPoint(1, 1));
        Assert.Equal(1, session.Vertices[1].Latitude);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.MoveVertex(5, new FieldGeoPoint(0, 0)));
    }

    [Fact]
    public void Line_geojson_is_a_linestring_with_lon_lat_order()
    {
        var session = new GeometryCaptureSession(CapturedGeometryType.Line);
        session.AddVertex(new FieldGeoPoint(45, -122));
        session.AddVertex(new FieldGeoPoint(46, -123));

        using var doc = JsonDocument.Parse(session.ToGeoJson());
        var root = doc.RootElement;

        Assert.Equal("LineString", root.GetProperty("type").GetString());
        var coords = root.GetProperty("coordinates");
        Assert.Equal(-122, coords[0][0].GetDouble()); // lon first
        Assert.Equal(45, coords[0][1].GetDouble());
    }

    [Fact]
    public void Polygon_needs_three_vertices_and_closes_the_ring()
    {
        var session = new GeometryCaptureSession(CapturedGeometryType.Polygon);
        session.AddVertex(new FieldGeoPoint(0, 0));
        session.AddVertex(new FieldGeoPoint(0, 1));
        Assert.False(session.IsComplete);
        Assert.Throws<InvalidOperationException>(() => session.ToGeoJson());

        session.AddVertex(new FieldGeoPoint(1, 1));
        Assert.True(session.IsComplete);

        using var doc = JsonDocument.Parse(session.ToGeoJson());
        var ring = doc.RootElement.GetProperty("coordinates")[0];

        Assert.Equal("Polygon", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(4, ring.GetArrayLength()); // 3 vertices + closing point
        Assert.Equal(ring[0][0].GetDouble(), ring[3][0].GetDouble());
        Assert.Equal(ring[0][1].GetDouble(), ring[3][1].GetDouble());
    }

    [Fact]
    public void Polygon_applies_geojson_to_a_field_and_seeds_record_location()
    {
        var session = new GeometryCaptureSession(CapturedGeometryType.Polygon);
        session.AddVertex(new FieldGeoPoint(0, 0));
        session.AddVertex(new FieldGeoPoint(0, 1));
        session.AddVertex(new FieldGeoPoint(1, 1));

        var record = new FieldRecord { RecordId = "r", FormId = "f" };
        session.ApplyTo(record, "boundary");

        Assert.Contains("Polygon", (string)record.Values["boundary"]!);
        Assert.NotNull(record.Location); // first vertex seeded for mapping
    }

    [Fact]
    public void Gps_averaging_means_position_and_tightens_accuracy()
    {
        var averager = new GpsAverager();
        averager.Add(new FieldGeoPoint(10.0, 20.0, 4));
        averager.Add(new FieldGeoPoint(10.2, 20.2, 4));
        averager.Add(new FieldGeoPoint(9.8, 19.8, 4));
        averager.Add(new FieldGeoPoint(10.0, 20.0, 4));

        var avg = averager.Average();
        Assert.Equal(4, averager.SampleCount);
        Assert.Equal(10.0, avg.Latitude, 3);
        Assert.Equal(20.0, avg.Longitude, 3);
        // mean accuracy 4m, reduced by sqrt(4)=2 -> 2m.
        Assert.Equal(2.0, avg.AccuracyMeters!.Value, 3);
    }

    [Fact]
    public void Averaged_vertex_feeds_the_capture_session()
    {
        var averager = new GpsAverager();
        averager.Add(new FieldGeoPoint(5, 5));
        averager.Add(new FieldGeoPoint(5, 5));

        var session = new GeometryCaptureSession(CapturedGeometryType.Point);
        session.AddAveragedVertex(averager);

        Assert.Equal(5, session.Vertices[0].Latitude);
        Assert.Throws<InvalidOperationException>(() => new GpsAverager().Average());
    }
}
