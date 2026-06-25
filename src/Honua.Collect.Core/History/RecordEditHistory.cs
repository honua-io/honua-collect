using System.Collections;
using System.Text.Json;
using Honua.Collect.Core.Field;

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

        var changes = ComputeChanges(before, after);
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
        => ReverseApplyAfter(current, _edits, toSequence);

    /// <summary>
    /// Reconstructs the values as they were immediately after the edit with sequence
    /// <paramref name="toSequence"/> by reverse-applying every later edit in
    /// <paramref name="edits"/> to <paramref name="current"/>. This is the single,
    /// shared reverse-apply both the in-memory history and the durable
    /// <see cref="RecordEditLog"/>-backed revert path use, so neither hand-rolls its
    /// own (drift-prone) reverse loop. Edits are located by their
    /// <see cref="RecordEdit.Sequence"/>, not by list position, so a non-dense window
    /// (only the recent edits loaded) still reverts correctly. Pass -1 to revert all
    /// the way to the original.
    /// </summary>
    /// <param name="current">The record's current values.</param>
    /// <param name="edits">The edits to reverse, in any order (sequence is authoritative).</param>
    /// <param name="toSequence">The edit sequence to revert to (inclusive); -1 for the original.</param>
    /// <returns>The reconstructed values (a new map; <paramref name="current"/> is not mutated).</returns>
    public static IReadOnlyDictionary<string, object?> ReverseApplyAfter(
        IReadOnlyDictionary<string, object?> current,
        IReadOnlyList<RecordEdit> edits,
        long toSequence)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(edits);

        var values = new Dictionary<string, object?>(current);

        // Reverse-apply later edits highest-sequence-first so each edit's OldValue
        // overwrites the NewValue a more-recent edit may have left. Order by sequence
        // rather than trusting list order or assuming a dense [0..n) window.
        foreach (var edit in edits.Where(e => e.Sequence > toSequence).OrderByDescending(e => e.Sequence))
        {
            ReverseApply(values, edit);
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

    /// <summary>
    /// Computes the field-level changes that turn <paramref name="before"/> into
    /// <paramref name="after"/>, using the same missing-value/list-contents
    /// equality as <see cref="Record"/>. Exposed so the durable edit log
    /// (<see cref="RecordEditLog"/>) diffs identically to the in-memory history
    /// rather than carrying a second, drifting comparison.
    /// </summary>
    /// <param name="before">The record's values before the edit.</param>
    /// <param name="after">The record's values after the edit.</param>
    /// <returns>One <see cref="FieldChange"/> per genuinely-changed field.</returns>
    public static IReadOnlyList<FieldChange> ComputeChanges(
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

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

        // Unwrap scalar JsonElements (how the SQLite record/history stores rehydrate
        // values) to their natural CLR shape so a live JsonElement compares equal to
        // an in-memory long/double/bool/string of the same value — e.g. after a
        // revert restores a typed value, the next diff sees no spurious change.
        left = Unwrap(left);
        right = Unwrap(right);

        if (left is string ls && right is string rs)
        {
            return string.Equals(ls, rs, StringComparison.Ordinal);
        }

        // Numbers compare by value across representations (5L == 5d == JSON 5).
        if (FieldValues.TryAsDouble(left, out var ld) && FieldValues.TryAsDouble(right, out var rd))
        {
            return ld.Equals(rd);
        }

        if (left is bool lb && right is bool rb)
        {
            return lb == rb;
        }

        if (left is IEnumerable leftSeq and not string && right is IEnumerable rightSeq and not string)
        {
            return leftSeq.Cast<object?>()
                .Select(Unwrap)
                .SequenceEqual(rightSeq.Cast<object?>().Select(Unwrap), ValueEqualityComparer.Instance);
        }

        return Equals(left, right);
    }

    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.Array } a => !a.EnumerateArray().Any(),
        JsonElement => false,
        IEnumerable e => !e.Cast<object?>().Any(),
        _ => false,
    };

    // Scalar JsonElements (string/number/bool/null) become their natural CLR value so
    // downstream equality and reverse-apply don't have to special-case JSON wrappers.
    // Arrays/objects are left as JsonElement and handled by the collection paths.
    private static object? Unwrap(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Number } e => UnwrapNumber(e),
        _ => value,
    };

    // Box to object per-branch: a `cond ? long : double` ternary unifies to double and
    // would widen an integral value, so keep the branches separate.
    private static object UnwrapNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var l))
        {
            return l;
        }

        return element.GetDouble();
    }

    private sealed class ValueEqualityComparer : IEqualityComparer<object?>
    {
        public static readonly ValueEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y) => ValuesEqual(x, y);

        public int GetHashCode(object? obj) => 0; // equality-only use (SequenceEqual)
    }
}
