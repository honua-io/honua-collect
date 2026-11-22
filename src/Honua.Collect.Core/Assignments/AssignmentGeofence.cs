// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Geometry;

namespace Honua.Collect.Core.Assignments;

public static class AssignmentGeofence
{
    public static Geofence? For(FieldAssignment assignment, double radiusMeters)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return assignment.Location is { } location ? new Geofence(location, radiusMeters) : null;
    }

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
