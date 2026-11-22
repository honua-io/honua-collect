// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Text.Json;
using Honua.Collect.Core.Field;

namespace Honua.Collect.Core.History;

public sealed class RecordEditHistory
{
    private readonly List<RecordEdit> _edits = [];

    public IReadOnlyList<RecordEdit> Edits => _edits;

    public int Count => _edits.Count;

    public bool HasPostSyncEdits => _edits.Any(e => e.AfterSync);

    public RecordEdit? Last => _edits.Count == 0 ? null : _edits[^1];

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

    public IReadOnlyDictionary<string, object?> RevertTo(IReadOnlyDictionary<string, object?> current, long toSequence)
        => ReverseApplyAfter(current, _edits, toSequence);

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

    // Box to object per-branch: a `cond ? long: double` ternary unifies to double and
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
