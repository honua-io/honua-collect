using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Forms;

/// <summary>
/// Optional row-count bounds for a repeatable section (Survey123 / Fulcrum
/// min/max repeats — BACKLOG F-depth). The SDK <see cref="FormSection"/> in
/// package 1.1.0 does not carry these, so they are supplied product-side (the
/// form package wire format already declares <c>minInstances</c>/<c>maxInstances</c>)
/// and enforced by <see cref="FormSession.Validate"/>.
/// </summary>
/// <param name="Min">Minimum required rows, or <see langword="null"/> for no lower bound.</param>
/// <param name="Max">Maximum allowed rows, or <see langword="null"/> for no upper bound.</param>
public sealed record RepeatBounds(int? Min = null, int? Max = null);

/// <summary>
/// A repeatable section and the rows captured against it (Survey123 "repeat" /
/// Fulcrum "repeatable section"). The group owns the list of
/// <see cref="RepeatInstance"/> rows, lets the UI add and remove them, and
/// persists them back into the parent record so they travel with it through
/// validation, export, and sync.
/// </summary>
/// <remarks>
/// Rows are stored on the parent record's native <see cref="FieldRecord.Repeats"/>
/// (keyed by section id, each row a <see cref="FieldRepeatInstance"/>), so repeats
/// are part of the portable SDK record contract and round-trip through
/// serialization, sync, and export rather than relying on a product-side
/// convention in the flat <see cref="FieldRecord.Values"/> bag.
/// </remarks>
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

    /// <summary>Section identifier this group repeats.</summary>
    public string SectionId => _section.SectionId;

    /// <summary>Section label for the UI.</summary>
    public string Label => _section.Label;

    /// <summary>Optional row-count bounds enforced by <see cref="FormSession.Validate"/>; <see langword="null"/> when unbounded.</summary>
    public RepeatBounds? Bounds { get; }

    /// <summary>The captured rows, in order.</summary>
    public IReadOnlyList<RepeatInstance> Instances => _instances;

    /// <summary>Number of captured rows.</summary>
    public int Count => _instances.Count;

    /// <summary>Adds a new, empty row and returns it.</summary>
    /// <returns>The new row.</returns>
    public RepeatInstance AddInstance() => Add(seed: null);

    /// <summary>Removes a row by its instance id.</summary>
    /// <param name="instanceId">Row identifier.</param>
    /// <returns><see langword="true"/> if a row was removed.</returns>
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

    /// <summary>Writes the rows back into the parent record's native repeat store.</summary>
    /// <param name="parent">Parent record to persist into.</param>
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

    /// <summary>Reads previously-persisted rows for a section from a parent record.</summary>
    /// <param name="parent">Parent record.</param>
    /// <param name="sectionId">Repeatable section id.</param>
    /// <returns>The stored rows, or an empty sequence.</returns>
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
