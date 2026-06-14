namespace Honua.Collect.Core.History;

/// <summary>A single field's change within an edit: its value before and after.</summary>
/// <param name="FieldId">The field that changed.</param>
/// <param name="OldValue">The value before the edit (null/missing when newly set).</param>
/// <param name="NewValue">The value after the edit (null/missing when cleared).</param>
public sealed record FieldChange(string FieldId, object? OldValue, object? NewValue);

/// <summary>
/// One recorded edit to a record: who changed what, when, and whether it happened
/// after the record had already synced (the "I synced and now want to fix it"
/// case #38 turns into a feature). Edits are append-only, so the log is a durable,
/// reviewable change history.
/// </summary>
/// <param name="Sequence">Zero-based position in the record's edit history.</param>
/// <param name="TimestampUtc">When the edit was made.</param>
/// <param name="EditorUserId">Who made it.</param>
/// <param name="Changes">The field-level changes captured by this edit.</param>
/// <param name="AfterSync">Whether the edit was made after the record had synced.</param>
/// <param name="Note">Optional human note (e.g. "revert", "fixed serial number").</param>
public sealed record RecordEdit(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string EditorUserId,
    IReadOnlyList<FieldChange> Changes,
    bool AfterSync,
    string? Note = null);
