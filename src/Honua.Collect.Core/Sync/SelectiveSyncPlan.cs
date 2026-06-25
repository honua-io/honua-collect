using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// A geographic bounding box a user has opted to sync, in WGS84 decimal degrees.
/// Use this for layers whose features are in lat/lon; for a projected layer use
/// <see cref="SyncExtent"/> so coordinates are filtered in the layer's own CRS
/// (the #302 lesson).
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
/// A record-age / date window for selective sync (BACKLOG S2): only records whose
/// timestamp falls inside the window sync. The timestamp tested is the record's
/// <see cref="FieldRecord.SubmittedAtUtc"/> when present, otherwise its
/// <see cref="FieldRecord.CreatedAtUtc"/>, so both freshly captured drafts and
/// already-submitted records filter sensibly.
/// </summary>
public sealed record SyncDateWindow
{
    /// <summary>Inclusive earliest timestamp; <see langword="null"/> means no lower bound.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Inclusive latest timestamp; <see langword="null"/> means no upper bound.</summary>
    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>
    /// Maximum record age relative to "now"; records older than this are excluded.
    /// Composes with the absolute bounds (all configured constraints must hold).
    /// </summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>A window that admits only records no older than <paramref name="age"/>.</summary>
    /// <param name="age">Maximum age from now.</param>
    /// <returns>The window.</returns>
    public static SyncDateWindow WithinLast(TimeSpan age) => new() { MaxAge = age };

    /// <summary>A window bounded by an absolute earliest timestamp.</summary>
    /// <param name="notBefore">Inclusive earliest timestamp.</param>
    /// <returns>The window.</returns>
    public static SyncDateWindow Since(DateTimeOffset notBefore) => new() { NotBefore = notBefore };

    /// <summary>Whether a record's timestamp falls inside the window, as of <paramref name="now"/>.</summary>
    /// <param name="record">Record to test.</param>
    /// <param name="now">The reference "now" for <see cref="MaxAge"/>.</param>
    /// <returns><see langword="true"/> when the record is in the window.</returns>
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

/// <summary>
/// Sync opt-in for a single layer (BACKLOG S2): whether it syncs at all, and which
/// <em>subset</em> of its records to sync. With no constraints the whole layer
/// syncs; any combination of constraints restricts sync to records that satisfy
/// <strong>all</strong> of them:
/// <list type="bullet">
///   <item>spatial — <see cref="Areas"/> (WGS84) or <see cref="Extent"/> (layer CRS),</item>
///   <item>attribute — a <see cref="Where"/> predicate,</item>
///   <item>date/age — a <see cref="DateWindow"/>,</item>
///   <item>explicit selection — a <see cref="RecordIds"/> set.</item>
/// </list>
/// </summary>
public sealed record LayerSyncScope
{
    /// <summary>Layer key this scope applies to (matches the mobile engine's layer keys).</summary>
    public required string LayerKey { get; init; }

    /// <summary>Whether the layer participates in sync at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>WGS84 areas to restrict sync to. Empty means no geographic restriction.</summary>
    public IReadOnlyList<SyncAreaBounds> Areas { get; init; } = [];

    /// <summary>
    /// A spatial extent in the layer's own CRS to restrict sync to. Use this
    /// instead of <see cref="Areas"/> for projected layers; the record's location
    /// must be supplied as a projected <see cref="PlanarPoint"/> via the
    /// <c>project</c> delegate so coordinates are never funnelled through WGS84.
    /// </summary>
    public SyncExtent? Extent { get; init; }

    /// <summary>An attribute <c>where</c>-style predicate to restrict sync to.</summary>
    public SyncAttributeFilter? Where { get; init; }

    /// <summary>A record-age / date window to restrict sync to.</summary>
    public SyncDateWindow? DateWindow { get; init; }

    /// <summary>
    /// An explicit selection set of record ids. When non-empty, only records whose
    /// <see cref="FieldRecord.RecordId"/> is in the set sync. Ids are matched
    /// ordinally.
    /// </summary>
    public IReadOnlySet<string>? RecordIds { get; init; }

    /// <summary>Whether this scope imposes any record-level restriction beyond layer opt-in.</summary>
    public bool HasRecordFilter
        => Areas.Count > 0 || Extent is not null || Where is not null
           || DateWindow is not null || RecordIds is { Count: > 0 };
}

/// <summary>
/// A user's selective-sync configuration (BACKLOG S2): which layers to sync and
/// which subset of each layer's records — by spatial extent, attribute filter,
/// record age/date, or an explicit selection set. The decision is pure so the one
/// plan drives both the download filter (the pull) and the upload gate (the push),
/// and it composes with the mobile sync engine's per-layer conflict policy rules.
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

    /// <summary>The scopes that make up this plan, keyed by layer.</summary>
    public IReadOnlyCollection<LayerSyncScope> Scopes => _scopes.Values;

    /// <summary>Whether a layer participates in sync at all.</summary>
    /// <param name="layerKey">Layer key.</param>
    /// <returns><see langword="true"/> when the layer syncs.</returns>
    public bool IncludesLayer(string layerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        return _scopes.TryGetValue(layerKey, out var scope) ? scope.Enabled : IncludeUnlistedLayers;
    }

    /// <summary>The scope configured for a layer, if any.</summary>
    /// <param name="layerKey">Layer key.</param>
    /// <returns>The scope, or <see langword="null"/> when the layer is unlisted.</returns>
    public LayerSyncScope? ScopeFor(string layerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        return _scopes.TryGetValue(layerKey, out var scope) ? scope : null;
    }

    /// <summary>
    /// The GeoServices <c>where</c> clause to send to the server when pulling a
    /// layer, so the server pre-filters by attribute. Defaults to <c>1=1</c> (all
    /// rows) when the layer has no attribute filter. The result is always
    /// re-applied locally by <see cref="IncludesRecord(string, FieldRecord, Func{FieldRecord, PlanarPoint?}?, DateTimeOffset?)"/>,
    /// so the device never relies on the server having filtered.
    /// </summary>
    /// <param name="layerKey">Layer to build the clause for.</param>
    /// <returns>The <c>where</c> clause text.</returns>
    public string WhereClauseFor(string layerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        return _scopes.TryGetValue(layerKey, out var scope) && scope.Where is { } filter
            ? filter.Where
            : "1=1";
    }

    /// <summary>
    /// Whether a record is in sync scope, considering the layer opt-in and a WGS84
    /// area restriction only. Kept for callers that only have a record's lat/lon;
    /// prefer <see cref="IncludesRecord(string, FieldRecord, Func{FieldRecord, PlanarPoint?}?, DateTimeOffset?)"/>
    /// for the full filter.
    /// </summary>
    /// <param name="layerKey">Layer the record belongs to.</param>
    /// <param name="location">Record location (WGS84), when known.</param>
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
            // No WGS84 area restriction. Note this overload only sees a location,
            // so non-spatial restrictions (attribute/date/selection) are not applied
            // here; callers needing the full filter use the FieldRecord overload.
            return true;
        }

        return location is not null && scope.Areas.Any(area => area.Contains(location));
    }

    /// <summary>
    /// Whether a record is in sync scope, considering the layer opt-in and every
    /// configured restriction (spatial, attribute, date/age, explicit selection).
    /// A record must satisfy <em>all</em> configured constraints to sync.
    /// </summary>
    /// <param name="layerKey">Layer the record belongs to.</param>
    /// <param name="record">The record to test.</param>
    /// <param name="project">
    /// Projects the record into the layer's CRS for an <see cref="SyncExtent"/>
    /// test. Required only when the scope has an <see cref="LayerSyncScope.Extent"/>;
    /// returns <see langword="null"/> when the record has no placeable geometry.
    /// </param>
    /// <param name="now">Reference time for an age window; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <returns><see langword="true"/> when the record should sync.</returns>
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
