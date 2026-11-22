// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Forms;

public sealed record RepeatBounds(int? Min = null, int? Max = null);

public sealed class RepeatGroup
{
    private readonly FormSection _section;
    private readonly FormDefinition _instanceForm;
    private readonly List<RepeatInstance> _instances = [];
    private int _nextOrdinal;

    internal RepeatGroup(
        FormDefinition parentForm,
        FormSection section,
        IEnumerable<IReadOnlyDictionary<string, object?>> seededRows,
        RepeatBounds? bounds = null)
    {
        _section = section;
        Bounds = bounds;

        // A single-section, non-repeatable form that each row is captured against.
        _instanceForm = new FormDefinition
        {
            FormId = $"{parentForm.FormId}::{section.SectionId}",
            Name = section.Label,
            Sections = [section with { Repeatable = false }],
        };

        foreach (var row in seededRows)
        {
            Add(row);
        }
    }

    public string SectionId => _section.SectionId;

    public string Label => _section.Label;

    public RepeatBounds? Bounds { get; }

    public IReadOnlyList<RepeatInstance> Instances => _instances;

    public int Count => _instances.Count;

    public RepeatInstance AddInstance() => Add(seed: null);

    public bool RemoveInstance(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return _instances.RemoveAll(i => string.Equals(i.InstanceId, instanceId, StringComparison.Ordinal)) > 0;
    }

    private RepeatInstance Add(IReadOnlyDictionary<string, object?>? seed)
    {
        var instanceId = $"{SectionId}#{_nextOrdinal++}";
        var record = new FieldRecord { RecordId = instanceId, FormId = _instanceForm.FormId };

        if (seed is not null)
        {
            foreach (var pair in seed)
            {
                record.Values[pair.Key] = pair.Value;
            }
        }

        var instance = new RepeatInstance(instanceId, FormSession.Open(_instanceForm, record));
        _instances.Add(instance);
        return instance;
    }

    internal void PersistInto(FieldRecord parent)
    {
        // Retire any value left by the former flat-Values storage convention.
        parent.Values.Remove(SectionId);

        if (_instances.Count == 0)
        {
            parent.Repeats.Remove(SectionId);
            return;
        }

        parent.Repeats[SectionId] = _instances
            .Select(i => new FieldRepeatInstance
            {
                Values = new Dictionary<string, object?>(i.SnapshotValues(), StringComparer.OrdinalIgnoreCase),
            })
            .ToList<FieldRepeatInstance>();
    }

    internal static IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(FieldRecord parent, string sectionId)
    {
        if (parent.Repeats.TryGetValue(sectionId, out var rows) && rows is not null)
        {
            return rows
                .Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(r.Values, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        return [];
    }
}
