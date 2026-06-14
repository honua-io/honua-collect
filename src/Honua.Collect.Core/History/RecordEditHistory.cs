using System.Collections;

namespace Honua.Collect.Core.History;

/// <summary>
/// An append-only, per-record edit history (BACKLOG, #38) — the durable change log
/// behind "your edits are never trapped or lost". It captures each edit as a
/// field-level diff (who/when/what, before vs after), keeps recording edits made
/// <em>after</em> a record has synced (no post-sync lockout), and can reconstruct
/// any earlier version so a mistaken change can be undone.
/// </summary>
/// <remarks>
/// Records are represented as field-id → value maps (a record's <c>Values</c>), so
/// the history is decoupled from the storage/transport layer that persists it.
/// Revert is pure: it returns the reconstructed values and records nothing, so the
/// caller can append the revert itself as a new, audited edit.
/// </remarks>
public sealed class RecordEditHistory
{
    private readonly List<RecordEdit> _edits = [];

    /// <summary>All edits, oldest first.</summary>
    public IReadOnlyList<RecordEdit> Edits => _edits;

    /// <summary>Number of recorded edits.</summary>
    public int Count => _edits.Count;

    /// <summary>Whether any edit was made after the record had synced.</summary>
    public bool HasPostSyncEdits => _edits.Any(e => e.AfterSync);

    /// <summary>The most recent edit, or null when the history is empty.</summary>
    public RecordEdit? Last => _edits.Count == 0 ? null : _edits[^1];

    /// <summary>
    /// Records an edit by diffing the record's values before and after. When nothing
    /// actually changed, no edit is appended and null is returned.
    /// </summary>
    /// <param name="before">The record's values before the edit.</param>
    /// <param name="after">The record's values after the edit.</param>
    /// <param name="editorUserId">Who made the edit.</param>
    /// <param name="timestampUtc">When the edit was made.</param>
    /// <param name="afterSync">Whether the record had already synced.</param>
    /// <param name="note">Optional human note.</param>
    /// <returns>The appended edit, or null when there was no change.</returns>
    public RecordEdit? Record(
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after,
        string editorUserId,
        DateTimeOffset timestampUtc,
        bool afterSync = false,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorUserId);

        var changes = Diff(before, after);
        if (changes.Count == 0)
        {
            return null;
        }

        var edit = new RecordEdit(_edits.Count, timestampUtc, editorUserId, changes, afterSync, note);
        _edits.Add(edit);
        return edit;
    }

    /// <summary>
    /// Reconstructs the record's values as they were immediately after the edit at
    /// <paramref name="toSequence"/>, by reverse-applying every later edit to
    /// <paramref name="current"/>. Pass -1 to revert all the way to the original.
    /// </summary>
    /// <param name="current">The record's current values.</param>
    /// <param name="toSequence">The edit sequence to revert to (inclusive); -1 for the original.</param>
    /// <returns>The reconstructed values (a new map; <paramref name="current"/> is not mutated).</returns>
    public IReadOnlyDictionary<string, object?> RevertTo(IReadOnlyDictionary<string, object?> current, long toSequence)
    {
        ArgumentNullException.ThrowIfNull(current);

        var values = new Dictionary<string, object?>(current);
        for (var i = _edits.Count - 1; i > toSequence; i--)
        {
            ReverseApply(values, _edits[i]);
        }

        return values;
    }

    /// <summary>Reconstructs the values from before the most recent edit (undo last change).</summary>
    /// <param name="current">The record's current values.</param>
    /// <returns>The values with the last edit undone (a copy when there is no history).</returns>
    public IReadOnlyDictionary<string, object?> UndoLast(IReadOnlyDictionary<string, object?> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return RevertTo(current, _edits.Count - 2);
    }

    private static void ReverseApply(Dictionary<string, object?> values, RecordEdit edit)
    {
        foreach (var change in edit.Changes)
        {
            if (IsMissing(change.OldValue))
            {
                values.Remove(change.FieldId);
            }
            else
            {
                values[change.FieldId] = change.OldValue;
            }
        }
    }

    private static List<FieldChange> Diff(
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after)
    {
        var changes = new List<FieldChange>();
        foreach (var fieldId in before.Keys.Union(after.Keys, StringComparer.Ordinal))
        {
            before.TryGetValue(fieldId, out var oldValue);
            after.TryGetValue(fieldId, out var newValue);
            if (!ValuesEqual(oldValue, newValue))
            {
                changes.Add(new FieldChange(fieldId, oldValue, newValue));
            }
        }

        return changes;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        var leftMissing = IsMissing(left);
        var rightMissing = IsMissing(right);
        if (leftMissing || rightMissing)
        {
            return leftMissing && rightMissing;
        }

        if (left is string || right is string)
        {
            return Equals(left, right);
        }

        if (left is IEnumerable leftSeq && right is IEnumerable rightSeq)
        {
            return leftSeq.Cast<object?>().SequenceEqual(rightSeq.Cast<object?>());
        }

        return Equals(left, right);
    }

    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        IEnumerable e => !e.Cast<object?>().Any(),
        _ => false,
    };
}
