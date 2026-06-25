using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Records;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// A <see cref="SelectiveSyncPlan"/> bound to one layer (BACKLOG S2), so the pull
/// and push can ask a single yes/no question per record without re-passing the
/// layer key and projector each time. The pull (<see cref="FeaturePullService"/>)
/// uses it to drop server features outside the scope; the push uses it to gate
/// which local records upload — the same plan drives both directions, so a partial
/// sync is symmetric.
/// </summary>
public sealed class SelectiveSyncFilter
{
    private readonly SelectiveSyncPlan _plan;
    private readonly string _layerKey;
    private readonly Func<FieldRecord, PlanarPoint?>? _project;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Binds a plan to a layer.</summary>
    /// <param name="plan">The selective-sync plan.</param>
    /// <param name="layerKey">The layer this filter is scoped to.</param>
    /// <param name="project">
    /// Projects a record into the layer's CRS for a <see cref="SyncExtent"/> test;
    /// required only when the layer scope has an extent. Returns <see langword="null"/>
    /// when the record has no placeable geometry.
    /// </param>
    /// <param name="now">Clock for date/age windows; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public SelectiveSyncFilter(
        SelectiveSyncPlan plan,
        string layerKey,
        Func<FieldRecord, PlanarPoint?>? project = null,
        Func<DateTimeOffset>? now = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);
        _layerKey = layerKey;
        _project = project;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The layer this filter is scoped to.</summary>
    public string LayerKey => _layerKey;

    /// <summary>Whether this layer participates in sync at all.</summary>
    public bool LayerEnabled => _plan.IncludesLayer(_layerKey);

    /// <summary>The GeoServices <c>where</c> clause to pull this layer with.</summary>
    public string WhereClause => _plan.WhereClauseFor(_layerKey);

    /// <summary>Whether a local record is in scope to push/keep.</summary>
    /// <param name="record">The record to test.</param>
    /// <returns><see langword="true"/> when the record should sync.</returns>
    public bool Includes(FieldRecord record)
        => _plan.IncludesRecord(_layerKey, record, _project, _now());

    /// <summary>Whether a local record entry is in scope to push.</summary>
    /// <param name="entry">The tracked record entry to test.</param>
    /// <returns><see langword="true"/> when the entry should sync.</returns>
    public bool Includes(CollectRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Includes(entry.Record);
    }

    /// <summary>Whether a server feature pulled for this layer is in scope to merge.</summary>
    /// <param name="pulled">The pulled server feature.</param>
    /// <returns><see langword="true"/> when the feature should be merged.</returns>
    public bool IncludesPulled(PulledRecord pulled)
    {
        ArgumentNullException.ThrowIfNull(pulled);
        return Includes(pulled.Record);
    }

    /// <summary>
    /// Filters a set of local entries to those in scope to push, leaving the rest
    /// untouched in the caller's collection (the push gate for a partial sync).
    /// </summary>
    /// <param name="entries">The candidate entries.</param>
    /// <returns>The entries that should upload.</returns>
    public IReadOnlyList<CollectRecordEntry> SelectForPush(IEnumerable<CollectRecordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return LayerEnabled
            ? entries.Where(Includes).ToList()
            : [];
    }
}
