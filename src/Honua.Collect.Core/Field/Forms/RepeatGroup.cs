using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Forms;

/// <summary>
/// A repeatable section and the rows captured against it (Survey123 "repeat" /
/// Fulcrum "repeatable section"). The group owns the list of
/// <see cref="RepeatInstance"/> rows, lets the UI add and remove them, and
/// persists them back into the parent record so they travel with it through
/// validation, export, and sync.
/// </summary>
/// <remarks>
/// Rows are stored on the parent record under the section id as a
/// <c>List&lt;Dictionary&lt;string, object?&gt;&gt;</c>. This product-side
/// storage convention is what lets repeats work today over the flat SDK
/// <see cref="FieldRecord"/> contract; promoting it into the portable SDK record
/// contract (so repeats round-trip through server sync natively) is the
/// follow-up tracked for the next SDK package cut.
/// </remarks>
public sealed class RepeatGroup
{
    private readonly FormSection _section;
    private readonly FormDefinition _instanceForm;
    private readonly List<RepeatInstance> _instances = [];
    private int _nextOrdinal;

    internal RepeatGroup(FormDefinition parentForm, FormSection section, IEnumerable<IReadOnlyDictionary<string, object?>> seededRows)
    {
        _section = section;

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

    /// <summary>Writes the rows back into the parent record under the section id.</summary>
    /// <param name="parent">Parent record to persist into.</param>
    internal void PersistInto(FieldRecord parent)
    {
        if (_instances.Count == 0)
        {
            parent.Values[SectionId] = null;
            return;
        }

        parent.Values[SectionId] = _instances.Select(i => i.SnapshotValues()).ToList();
    }

    /// <summary>Reads previously-persisted rows for a section from a parent record.</summary>
    /// <param name="parent">Parent record.</param>
    /// <param name="sectionId">Repeatable section id.</param>
    /// <returns>The stored rows, or an empty sequence.</returns>
    internal static IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(FieldRecord parent, string sectionId)
    {
        if (!parent.Values.TryGetValue(sectionId, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            IEnumerable<IReadOnlyDictionary<string, object?>> typed => typed.ToList(),
            IEnumerable<IDictionary<string, object?>> dicts => dicts.Select(d => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(d, StringComparer.OrdinalIgnoreCase)).ToList(),
            _ => [],
        };
    }
}
