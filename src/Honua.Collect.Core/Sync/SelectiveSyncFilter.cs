// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Records;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

public sealed class SelectiveSyncFilter
{
    private readonly SelectiveSyncPlan _plan;
    private readonly string _layerKey;
    private readonly Func<FieldRecord, PlanarPoint?>? _project;
    private readonly Func<DateTimeOffset> _now;

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

    public string LayerKey => _layerKey;

    public bool LayerEnabled => _plan.IncludesLayer(_layerKey);

    public string WhereClause => _plan.WhereClauseFor(_layerKey);

    public bool Includes(FieldRecord record)
        => _plan.IncludesRecord(_layerKey, record, _project, _now());

    public bool Includes(CollectRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Includes(entry.Record);
    }

    public bool IncludesPulled(PulledRecord pulled)
    {
        ArgumentNullException.ThrowIfNull(pulled);
        return Includes(pulled.Record);
    }

    public IReadOnlyList<CollectRecordEntry> SelectForPush(IEnumerable<CollectRecordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return LayerEnabled
            ? entries.Where(Includes).ToList()
            : [];
    }
}
