// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

public sealed record SyncAreaBounds(double MinLatitude, double MinLongitude, double MaxLatitude, double MaxLongitude)
{
    public bool Contains(FieldGeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return point.Latitude >= MinLatitude && point.Latitude <= MaxLatitude
            && point.Longitude >= MinLongitude && point.Longitude <= MaxLongitude;
    }
}

public sealed record SyncDateWindow
{
    public DateTimeOffset? NotBefore { get; init; }

    public DateTimeOffset? NotAfter { get; init; }

    public TimeSpan? MaxAge { get; init; }

    public static SyncDateWindow WithinLast(TimeSpan age) => new() { MaxAge = age };

    public static SyncDateWindow Since(DateTimeOffset notBefore) => new() { NotBefore = notBefore };

    public bool Includes(FieldRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        var stamp = record.SubmittedAtUtc ?? record.CreatedAtUtc;

        if (NotBefore is { } lo && stamp < lo)
        {
            return false;
        }

        if (NotAfter is { } hi && stamp > hi)
        {
            return false;
        }

        if (MaxAge is { } age && now - stamp > age)
        {
            return false;
        }

        return true;
    }
}

public sealed record LayerSyncScope
{
    public required string LayerKey { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<SyncAreaBounds> Areas { get; init; } = [];

    public SyncExtent? Extent { get; init; }

    public SyncAttributeFilter? Where { get; init; }

    public SyncDateWindow? DateWindow { get; init; }

    public IReadOnlySet<string>? RecordIds { get; init; }

    public bool HasRecordFilter
        => Areas.Count > 0 || Extent is not null || Where is not null
           || DateWindow is not null || RecordIds is { Count: > 0 };
}

public sealed class SelectiveSyncPlan
{
    private readonly Dictionary<string, LayerSyncScope> _scopes;

    public SelectiveSyncPlan(IEnumerable<LayerSyncScope> scopes, bool includeUnlistedLayers = false)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes.ToDictionary(s => s.LayerKey, StringComparer.OrdinalIgnoreCase);
        IncludeUnlistedLayers = includeUnlistedLayers;
    }

    public static SelectiveSyncPlan SyncEverything { get; } = new([], includeUnlistedLayers: true);

    public bool IncludeUnlistedLayers { get; }

    public IReadOnlyCollection<LayerSyncScope> Scopes => _scopes.Values;

    public bool IncludesLayer(string layerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        return _scopes.TryGetValue(layerKey, out var scope) ? scope.Enabled : IncludeUnlistedLayers;
    }

    public LayerSyncScope? ScopeFor(string layerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        return _scopes.TryGetValue(layerKey, out var scope) ? scope : null;
    }

    public string WhereClauseFor(string layerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        return _scopes.TryGetValue(layerKey, out var scope) && scope.Where is { } filter
            ? filter.Where
            : "1=1";
    }

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
            // No WGS84 area restriction. Note this overload only sees a location,
            // so non-spatial restrictions (attribute/date/selection) are not applied
            // here; callers needing the full filter use the FieldRecord overload.
            return true;
        }

        return location is not null && scope.Areas.Any(area => area.Contains(location));
    }

    public bool IncludesRecord(
        string layerKey,
        FieldRecord record,
        Func<FieldRecord, PlanarPoint?>? project = null,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        ArgumentNullException.ThrowIfNull(record);

        if (!_scopes.TryGetValue(layerKey, out var scope))
        {
            return IncludeUnlistedLayers;
        }

        if (!scope.Enabled)
        {
            return false;
        }

        // Explicit selection set: an id outside the set is never synced.
        if (scope.RecordIds is { Count: > 0 } ids && !ids.Contains(record.RecordId))
        {
            return false;
        }

        // WGS84 areas (only when configured).
        if (scope.Areas.Count > 0)
        {
            if (record.Location is not { } loc || !scope.Areas.Any(area => area.Contains(loc)))
            {
                return false;
            }
        }

        // Projected extent in the layer CRS (only when configured).
        if (scope.Extent is { } extent)
        {
            var projected = project?.Invoke(record);
            if (projected is not { } point || !extent.Contains(point))
            {
                return false;
            }
        }

        // Attribute where-predicate.
        if (scope.Where is { } filter && !filter.Matches(record))
        {
            return false;
        }

        // Date/age window.
        if (scope.DateWindow is { } window && !window.Includes(record, now ?? DateTimeOffset.UtcNow))
        {
            return false;
        }

        return true;
    }
}
