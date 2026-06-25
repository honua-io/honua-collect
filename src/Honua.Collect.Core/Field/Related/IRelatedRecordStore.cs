using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Related;

/// <summary>
/// Durable store for the one-to-many links between a parent record and its child
/// records (BACKLOG F4 — related tables / record links). Links live in a side
/// table so a related-records screen can list a parent's children, and so
/// referential integrity (cascade or restrict on parent delete) can be enforced
/// without scanning every record's value blob.
/// </summary>
public interface IRelatedRecordStore
{
    /// <summary>Persists a link from a parent record's field to a child record (idempotent).</summary>
    /// <param name="parentRecordId">Owning parent record id.</param>
    /// <param name="fieldId">Record-link field on the parent.</param>
    /// <param name="link">The child link to store.</param>
    /// <param name="behavior">Referential-integrity policy for this relationship on parent delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LinkAsync(
        string parentRecordId,
        string fieldId,
        FieldRecordLinkValue link,
        RecordLinkBehavior behavior = RecordLinkBehavior.Cascade,
        CancellationToken ct = default);

    /// <summary>Removes a single parent→child link, if present.</summary>
    /// <param name="parentRecordId">Owning parent record id.</param>
    /// <param name="fieldId">Record-link field on the parent.</param>
    /// <param name="childRecordId">Child record id to unlink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if a link was removed.</returns>
    Task<bool> UnlinkAsync(string parentRecordId, string fieldId, string childRecordId, CancellationToken ct = default);

    /// <summary>Lists the children linked from a parent record's field, in insertion order.</summary>
    /// <param name="parentRecordId">Owning parent record id.</param>
    /// <param name="fieldId">Record-link field on the parent.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The linked child records.</returns>
    Task<IReadOnlyList<FieldRecordLinkValue>> ListAsync(string parentRecordId, string fieldId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a parent and enforces referential integrity over its linked children:
    /// <see cref="RecordLinkBehavior.Cascade"/> links are removed (and their child
    /// ids returned so the caller can delete the child records), while any
    /// <see cref="RecordLinkBehavior.Restrict"/> link still present makes the delete
    /// throw.
    /// </summary>
    /// <param name="parentRecordId">Parent record id being deleted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The child record ids freed by a cascade, for the caller to delete.</returns>
    /// <exception cref="RelatedRecordIntegrityException">
    /// The parent still has <see cref="RecordLinkBehavior.Restrict"/> children.
    /// </exception>
    Task<IReadOnlyList<string>> DeleteParentAsync(string parentRecordId, CancellationToken ct = default);
}

/// <summary>
/// Thrown when deleting a parent record would violate a
/// <see cref="RecordLinkBehavior.Restrict"/> relationship that still has children
/// (BACKLOG F4).
/// </summary>
public sealed class RelatedRecordIntegrityException : InvalidOperationException
{
    /// <summary>Creates the exception for a restricted parent delete.</summary>
    /// <param name="parentRecordId">The parent that could not be deleted.</param>
    /// <param name="childCount">How many restricted children still reference it.</param>
    public RelatedRecordIntegrityException(string parentRecordId, int childCount)
        : base($"Record '{parentRecordId}' still has {childCount} linked child record(s) under a restrict policy; unlink them before deleting.")
    {
        ParentRecordId = parentRecordId;
        ChildCount = childCount;
    }

    /// <summary>The parent that could not be deleted.</summary>
    public string ParentRecordId { get; }

    /// <summary>How many restricted children blocked the delete.</summary>
    public int ChildCount { get; }
}
