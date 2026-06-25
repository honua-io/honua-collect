namespace Honua.Collect.Core.Field.Related;

/// <summary>
/// Referential-integrity policy for a related-records relationship (BACKLOG F4 —
/// related tables / record links) when the parent record is deleted. Mirrors the
/// <c>ON DELETE</c> semantics Survey123/Fulcrum apply to their child tables.
/// </summary>
public enum RecordLinkBehavior
{
    /// <summary>
    /// Deleting the parent deletes its linked child records too — the default for
    /// owned children (an inspection's deficiencies, a pole's attachments) that have
    /// no meaning without their parent.
    /// </summary>
    Cascade = 0,

    /// <summary>
    /// Deleting the parent is refused while it still has linked children — for
    /// references to shared records that must be unlinked first.
    /// </summary>
    Restrict = 1,
}
