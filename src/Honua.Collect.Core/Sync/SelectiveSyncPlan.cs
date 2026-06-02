using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// A geographic bounding box a user has opted to sync, in decimal degrees.
/// </summary>
/// <param name="MinLatitude">Southern edge.</param>
/// <param name="MinLongitude">Western edge.</param>
/// <param name="MaxLatitude">Northern edge.</param>
/// <param name="MaxLongitude">Eastern edge.</param>
public sealed record SyncAreaBounds(double MinLatitude, double MinLongitude, double MaxLatitude, double MaxLongitude)
{
    /// <summary>Whether a point falls within (or on the edge of) this box.</summary>
    /// <param name="point">Point to test.</param>
    /// <returns><see langword="true"/> when the point is inside the box.</returns>
    public bool Contains(FieldGeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return point.Latitude >= MinLatitude && point.Latitude <= MaxLatitude
            && point.Longitude >= MinLongitude && point.Longitude <= MaxLongitude;
    }
}

/// <summary>
/// Sync opt-in for a single layer: whether it syncs at all, and optionally which
/// geographic areas within it. An empty <see cref="Areas"/> list means the whole
/// layer; any areas restrict sync to records that fall inside one of them.
/// </summary>
public sealed record LayerSyncScope
{
    /// <summary>Layer key this scope applies to (matches the mobile engine's layer keys).</summary>
    public required string LayerKey { get; init; }

    /// <summary>Whether the layer participates in sync at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Geographic areas to restrict sync to. Empty means the entire layer.</summary>
    public IReadOnlyList<SyncAreaBounds> Areas { get; init; } = [];
}

/// <summary>
/// A user's selective-sync configuration (BACKLOG S2): which layers and areas to
/// pull down and push up, instead of syncing everything. The decision is pure so
/// it can drive both the download filter and the upload gate, and it composes
/// with the mobile sync engine's per-layer conflict policy rules.
/// </summary>
public sealed class SelectiveSyncPlan
{
    private readonly Dictionary<string, LayerSyncScope> _scopes;

    /// <summary>Creates a plan from per-layer scopes.</summary>
    /// <param name="scopes">Per-layer sync scopes.</param>
    /// <param name="includeUnlistedLayers">
    /// Whether layers without an explicit scope sync by default. Defaults to
    /// <see langword="false"/> — selective sync is opt-in, so unlisted layers are
    /// excluded.
    /// </param>
    public SelectiveSyncPlan(IEnumerable<LayerSyncScope> scopes, bool includeUnlistedLayers = false)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes.ToDictionary(s => s.LayerKey, StringComparer.OrdinalIgnoreCase);
        IncludeUnlistedLayers = includeUnlistedLayers;
    }

    /// <summary>A plan that syncs everything (no filtering).</summary>
    public static SelectiveSyncPlan SyncEverything { get; } = new([], includeUnlistedLayers: true);

    /// <summary>Whether layers without an explicit scope sync by default.</summary>
    public bool IncludeUnlistedLayers { get; }

    /// <summary>Whether a layer participates in sync at all.</summary>
    /// <param name="layerKey">Layer key.</param>
    /// <returns><see langword="true"/> when the layer syncs.</returns>
    public bool IncludesLayer(string layerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        return _scopes.TryGetValue(layerKey, out var scope) ? scope.Enabled : IncludeUnlistedLayers;
    }

    /// <summary>
    /// Whether a record in a layer is in sync scope, considering both the layer
    /// opt-in and any area restriction.
    /// </summary>
    /// <param name="layerKey">Layer the record belongs to.</param>
    /// <param name="location">Record location, when known.</param>
    /// <returns><see langword="true"/> when the record should sync.</returns>
    public bool IncludesRecord(string layerKey, FieldGeoPoint? location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);

        if (!_scopes.TryGetValue(layerKey, out var scope))
        {
            return IncludeUnlistedLayers;
        }

        if (!scope.Enabled)
        {
            return false;
        }

        if (scope.Areas.Count == 0)
        {
            return true; // whole layer
        }

        // Area-restricted: a record with no location can't be placed in an area.
        return location is not null && scope.Areas.Any(area => area.Contains(location));
    }
}
