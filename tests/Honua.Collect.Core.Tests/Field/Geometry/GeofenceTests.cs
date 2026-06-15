using Honua.Collect.Core.Assignments;
using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Geometry;

public class GeofenceTests
{
    // ~0.001 deg latitude ~= 111 m; a point 0.005 deg north of centre is ~555 m away.
    private static readonly FieldGeoPoint Center = new(48.0, 11.0, null);
    private static readonly FieldGeoPoint Near = new(48.0005, 11.0, null);   // ~55 m N
    private static readonly FieldGeoPoint Far = new(48.01, 11.0, null);      // ~1.1 km N

    [Fact]
    public void Contains_is_true_inside_and_false_outside()
    {
        var fence = new Geofence(Center, radiusMeters: 100);

        Assert.True(fence.Contains(Center));
        Assert.True(fence.Contains(Near));   // ~55 m < 100 m
        Assert.False(fence.Contains(Far));   // ~1.1 km > 100 m
    }

    [Fact]
    public void DistanceMeters_is_a_plausible_great_circle_distance()
    {
        var fence = new Geofence(Center, 50);
        var distance = fence.DistanceMeters(Near);

        Assert.InRange(distance, 50, 60); // ~55 m
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    public void Radius_must_be_positive(double radius)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Geofence(Center, radius));
    }

    [Fact]
    public void Geofence_guards_null_points()
    {
        var fence = new Geofence(Center, 100);
        Assert.Throws<ArgumentNullException>(() => new Geofence(null!, 100));
        Assert.Throws<ArgumentNullException>(() => fence.DistanceMeters(null!));
    }

    [Fact]
    public void Monitor_fires_entered_once_then_exited_once()
    {
        var monitor = new GeofenceMonitor(new Geofence(Center, 100), triggerOnInitialInside: false);

        Assert.Equal(GeofenceTransition.None, monitor.Update(Far));     // baseline: outside
        Assert.Equal(GeofenceTransition.Entered, monitor.Update(Near)); // crossed in
        Assert.True(monitor.IsInside);
        Assert.Equal(GeofenceTransition.None, monitor.Update(Center));  // still inside, no re-fire
        Assert.Equal(GeofenceTransition.Exited, monitor.Update(Far));   // crossed out
        Assert.False(monitor.IsInside);
    }

    [Fact]
    public void Monitor_treats_an_initial_inside_fix_as_arrival_by_default()
    {
        var monitor = new GeofenceMonitor(new Geofence(Center, 100));

        Assert.Equal(GeofenceTransition.Entered, monitor.Update(Near)); // already on site
    }

    [Fact]
    public void Monitor_initial_inside_can_be_suppressed_and_reset()
    {
        var monitor = new GeofenceMonitor(new Geofence(Center, 100), triggerOnInitialInside: false);
        Assert.Equal(GeofenceTransition.None, monitor.Update(Near)); // baseline only
        Assert.True(monitor.IsInside);

        monitor.Reset();
        Assert.False(monitor.HasReading);
    }

    [Fact]
    public void Monitor_guards_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new GeofenceMonitor(null!));
        Assert.Throws<ArgumentNullException>(() => new GeofenceMonitor(new Geofence(Center, 100)).Update(null!));
    }

    // --- assignment bridge ----------------------------------------------------

    private static FieldAssignment Assignment(FieldGeoPoint? location) => new()
    {
        AssignmentId = "a1",
        FormId = "f1",
        AssignedToUserId = "u1",
        Title = "Inspect pole",
        Location = location,
    };

    [Fact]
    public void AssignmentGeofence_builds_a_fence_only_when_located()
    {
        Assert.NotNull(AssignmentGeofence.For(Assignment(Center), 100));
        Assert.Null(AssignmentGeofence.For(Assignment(null), 100));
    }

    [Fact]
    public void AssignmentGeofence_monitors_open_located_assignments_only()
    {
        Assert.NotNull(AssignmentGeofence.MonitorFor(Assignment(Center), 100)); // open + located
        Assert.Null(AssignmentGeofence.MonitorFor(Assignment(null), 100));      // no location

        var completed = Assignment(Center);
        completed.Start("rec-1");
        completed.Complete();
        Assert.Null(AssignmentGeofence.MonitorFor(completed, 100)); // closed
    }
}
