using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field;

/// <summary>
/// Manages the set of links stored on a parent record's <c>RecordLink</c> field
/// (BACKLOG F4 — related tables / record-link UX). Survey123 and Fulcrum both
/// let a record reference child records (a pole and its attached transformers,
/// an inspection and its deficiencies); the SDK models a single link with
/// <see cref="FieldRecordLinkValue"/>, and this manages the collection of them
/// the related-records screen binds to.
/// </summary>
/// <remarks>
/// Links are stored as a <see cref="List{T}"/> of <see cref="FieldRecordLinkValue"/>
/// in the parent record's value for the field, so they travel with the record
/// through validation, export, and sync without a separate side table.
/// </remarks>
public sealed class RecordLinkField
{
    private readonly FieldRecord _parent;
    private readonly List<FieldRecordLinkValue> _links;

    /// <summary>Opens the link manager for a record-link field on a parent record.</summary>
    /// <param name="parent">Parent record that owns the links.</param>
    /// <param name="field">The record-link field definition.</param>
    public RecordLinkField(FieldRecord parent, FormField field)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(field);

        if (field.Type != FormFieldType.RecordLink)
        {
            throw new ArgumentException($"Field '{field.FieldId}' is {field.Type}, not a RecordLink field.", nameof(field));
        }

        _parent = parent;
        Field = field;
        _links = ReadLinks(parent, field.FieldId);
    }

    /// <summary>The record-link field definition.</summary>
    public FormField Field { get; }

    /// <summary>The form child records must be captured against, when constrained.</summary>
    public string? ReferencedFormId => Field.ReferencedFormId;

    /// <summary>Current links, in insertion order.</summary>
    public IReadOnlyList<FieldRecordLinkValue> Links => _links;

    /// <summary>Number of linked records.</summary>
    public int Count => _links.Count;

    /// <summary>Adds a link to a child record, ignoring duplicates by record id.</summary>
    /// <param name="recordId">Child record identifier.</param>
    /// <param name="label">Display label captured at link time.</param>
    /// <param name="sourceId">Optional source identifier.</param>
    /// <returns><see langword="true"/> if a new link was added.</returns>
    public bool Add(string recordId, string? label = null, string? sourceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        if (_links.Any(l => string.Equals(l.RecordId, recordId, StringComparison.Ordinal)))
        {
            return false;
        }

        _links.Add(new FieldRecordLinkValue
        {
            RecordId = recordId,
            FormId = ReferencedFormId,
            SourceId = sourceId,
            Label = label,
        });

        Write();
        return true;
    }

    /// <summary>Links a captured child record, deriving the form id and a label.</summary>
    /// <param name="child">Child record to link.</param>
    /// <param name="label">Display label for the related-records list.</param>
    /// <returns><see langword="true"/> if a new link was added.</returns>
    public bool Add(FieldRecord child, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        return Add(child.RecordId, label, sourceId: null);
    }

    /// <summary>Removes the link to a child record.</summary>
    /// <param name="recordId">Child record identifier.</param>
    /// <returns><see langword="true"/> if a link was removed.</returns>
    public bool Remove(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        if (_links.RemoveAll(l => string.Equals(l.RecordId, recordId, StringComparison.Ordinal)) == 0)
        {
            return false;
        }

        Write();
        return true;
    }

    private void Write()
        => _parent.Values[Field.FieldId] = _links.Count == 0 ? null : new List<FieldRecordLinkValue>(_links);

    private static List<FieldRecordLinkValue> ReadLinks(FieldRecord parent, string fieldId)
    {
        if (!parent.Values.TryGetValue(fieldId, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            FieldRecordLinkValue single => [single],
            IEnumerable<FieldRecordLinkValue> many => [.. many],
            _ => [],
        };
    }
}
