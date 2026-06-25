using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Related;

/// <summary>
/// The one-to-many relationship between a parent record and the child records it
/// links through a <see cref="FormFieldType.RecordLink"/> field (BACKLOG F4 —
/// related tables / record links). This is the model a related-records screen
/// binds to and the form session surfaces per link field: it exposes the linked
/// children and the create / link / unlink / list operations over them, and
/// carries the <see cref="Behavior"/> that governs what happens to the children
/// when the parent is deleted.
/// </summary>
/// <remarks>
/// Links are held on the parent record's value (via <see cref="RecordLinkField"/>)
/// so they travel with validation, export, and sync; <see cref="IRelatedRecordStore"/>
/// mirrors them into a side table so a parent's children can be listed and
/// referential integrity enforced at delete time. <see cref="Create"/> mints a new
/// child <see cref="FieldRecord"/> against the <see cref="ReferencedFormId"/> and
/// links it in one step.
/// </remarks>
public sealed class RelatedRecords
{
    private readonly RecordLinkField _field;

    /// <summary>Opens the related-records model for a record-link field on a parent record.</summary>
    /// <param name="parent">Parent record that owns the relationship.</param>
    /// <param name="field">The record-link field definition.</param>
    /// <param name="behavior">Referential-integrity policy applied when the parent is deleted.</param>
    public RelatedRecords(FieldRecord parent, FormField field, RecordLinkBehavior behavior = RecordLinkBehavior.Cascade)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(field);

        Parent = parent;
        _field = new RecordLinkField(parent, field);
        Behavior = behavior;
    }

    /// <summary>The parent record that owns this relationship.</summary>
    public FieldRecord Parent { get; }

    /// <summary>The record-link field definition.</summary>
    public FormField Field => _field.Field;

    /// <summary>The form child records are captured against, when constrained.</summary>
    public string? ReferencedFormId => _field.ReferencedFormId;

    /// <summary>Referential-integrity policy applied to the children when the parent is deleted.</summary>
    public RecordLinkBehavior Behavior { get; }

    /// <summary>The linked child records, in insertion order.</summary>
    public IReadOnlyList<FieldRecordLinkValue> Children => _field.Links;

    /// <summary>Number of linked child records.</summary>
    public int Count => _field.Count;

    /// <summary>Links an existing child record by id, ignoring duplicates.</summary>
    /// <param name="childRecordId">Child record identifier.</param>
    /// <param name="label">Display label captured at link time.</param>
    /// <param name="sourceId">Optional source identifier.</param>
    /// <returns><see langword="true"/> if a new link was added.</returns>
    public bool Link(string childRecordId, string? label = null, string? sourceId = null)
        => _field.Add(childRecordId, label, sourceId);

    /// <summary>Links an already-captured child record, deriving its form id.</summary>
    /// <param name="child">Child record to link.</param>
    /// <param name="label">Display label for the related-records list.</param>
    /// <returns><see langword="true"/> if a new link was added.</returns>
    public bool Link(FieldRecord child, string? label = null)
        => _field.Add(child, label);

    /// <summary>
    /// Creates a brand-new child record against the <see cref="ReferencedFormId"/>
    /// and links it to the parent in one step (the common "add a related record"
    /// path on a related-records screen).
    /// </summary>
    /// <param name="childRecordId">Identifier for the new child record.</param>
    /// <param name="label">Display label for the related-records list.</param>
    /// <returns>The new, already-linked child record.</returns>
    public FieldRecord Create(string childRecordId, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childRecordId);

        var child = new FieldRecord
        {
            RecordId = childRecordId,
            FormId = ReferencedFormId ?? Field.FieldId,
        };

        _field.Add(child, label);
        return child;
    }

    /// <summary>Unlinks a child record from the parent.</summary>
    /// <param name="childRecordId">Child record identifier to unlink.</param>
    /// <returns><see langword="true"/> if a link was removed.</returns>
    public bool Unlink(string childRecordId)
        => _field.Remove(childRecordId);

    /// <summary>
    /// Persists this relationship's links into <paramref name="store"/> so the
    /// children can be listed independently and referential integrity enforced on
    /// parent delete (BACKLOG F4). Each current link is written under this
    /// relationship's <see cref="Behavior"/>.
    /// </summary>
    /// <param name="store">The related-record store to write through.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PersistAsync(IRelatedRecordStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        foreach (var link in _field.Links)
        {
            await store.LinkAsync(Parent.RecordId, Field.FieldId, link, Behavior, ct).ConfigureAwait(false);
        }
    }
}
