// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

public enum GeofenceTransition
{
    None,

    Entered,

    Exited,
}

public sealed class GeofenceMonitor
{
    private readonly Geofence _fence;
    private readonly bool _triggerOnInitialInside;
    private bool? _inside;

    public GeofenceMonitor(Geofence fence, bool triggerOnInitialInside = true)
    {
        _fence = fence ?? throw new ArgumentNullException(nameof(fence));
        _triggerOnInitialInside = triggerOnInitialInside;
    }

    public bool IsInside => _inside ?? false;

    public bool HasReading => _inside is not null;

    public GeofenceTransition Update(FieldGeoPoint location)
    {
        ArgumentNullException.ThrowIfNull(location);

        var nowInside = _fence.Contains(location);
        var wasInside = _inside ?? (_triggerOnInitialInside ? false : nowInside);
        _inside = nowInside;

        return (wasInside, nowInside) switch
        {
            (false, true) => GeofenceTransition.Entered,
            (true, false) => GeofenceTransition.Exited,
            _ => GeofenceTransition.None,
        };
    }

    public void Reset() => _inside = null;
}
