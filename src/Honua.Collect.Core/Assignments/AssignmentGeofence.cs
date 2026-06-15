using Honua.Collect.Core.Field.Geometry;

namespace Honua.Collect.Core.Assignments;

/// <summary>
/// Bridges a dispatched <see cref="FieldAssignment"/> to the geofencing primitives
/// (BACKLOG, #40): builds a fence around the assignment's location so a worker can
/// be flagged on arrival, completing the dispatch → navigate → arrive loop.
/// </summary>
public static class AssignmentGeofence
{
    /// <summary>
    /// Builds a geofence around an assignment's location, or null when the assignment
    /// has no location to fence.
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="radiusMeters">Arrival radius in metres.</param>
    /// <returns>The geofence, or null when the assignment has no location.</returns>
    public static Geofence? For(FieldAssignment assignment, double radiusMeters)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return assignment.Location is { } location ? new Geofence(location, radiusMeters) : null;
    }

    /// <summary>
    /// Creates a monitor for an open assignment with a location, or null when the
    /// assignment is closed or has no location (nothing to flag arrival for).
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="radiusMeters">Arrival radius in metres.</param>
    /// <returns>A monitor, or null when not applicable.</returns>
    public static GeofenceMonitor? MonitorFor(FieldAssignment assignment, double radiusMeters)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (!assignment.IsOpen)
        {
            return null;
        }

        var fence = For(assignment, radiusMeters);
        return fence is null ? null : new GeofenceMonitor(fence);
    }
}
