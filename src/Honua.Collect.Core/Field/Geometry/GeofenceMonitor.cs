using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

/// <summary>A geofence boundary crossing detected from successive location fixes.</summary>
public enum GeofenceTransition
{
    /// <summary>No boundary was crossed by this fix.</summary>
    None,

    /// <summary>The location moved into the fence (arrival).</summary>
    Entered,

    /// <summary>The location moved out of the fence (departure).</summary>
    Exited,
}

/// <summary>
/// Detects fence enter/exit transitions from a stream of location fixes (BACKLOG,
/// #40 — "trigger/flag on entering an assignment's location bounds"). Feed it each
/// fix; it fires <see cref="GeofenceTransition.Entered"/> once on arrival and
/// <see cref="GeofenceTransition.Exited"/> once on departure, so a caller can flag
/// the assignment without re-alerting on every fix inside.
/// </summary>
public sealed class GeofenceMonitor
{
    private readonly Geofence _fence;
    private readonly bool _triggerOnInitialInside;
    private bool? _inside;

    /// <summary>Creates a monitor for a fence.</summary>
    /// <param name="fence">The geofence to watch.</param>
    /// <param name="triggerOnInitialInside">
    /// When true (default), a first fix that is already inside counts as an arrival
    /// (<see cref="GeofenceTransition.Entered"/>) — the worker opened the app on site.
    /// When false, the first fix only establishes the baseline.
    /// </param>
    public GeofenceMonitor(Geofence fence, bool triggerOnInitialInside = true)
    {
        _fence = fence ?? throw new ArgumentNullException(nameof(fence));
        _triggerOnInitialInside = triggerOnInitialInside;
    }

    /// <summary>Whether the most recent fix was inside the fence.</summary>
    public bool IsInside => _inside ?? false;

    /// <summary>Whether any fix has been processed yet.</summary>
    public bool HasReading => _inside is not null;

    /// <summary>Processes a location fix and reports any boundary crossing.</summary>
    /// <param name="location">The new location fix.</param>
    /// <returns>The transition this fix caused, if any.</returns>
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

    /// <summary>Clears the monitor's state so the next fix re-establishes the baseline.</summary>
    public void Reset() => _inside = null;
}
