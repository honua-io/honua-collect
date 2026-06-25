using Honua.Collect.Core.History;

namespace Honua.Collect.Core.Tests.History;

/// <summary>
/// The in-memory <see cref="RecordEditHistory.RevertTo"/> and the durable revert path
/// must use ONE reverse-apply, so they can never drift. These tests exercise the
/// shared <see cref="RecordEditHistory.ReverseApplyAfter"/> directly, including the
/// sequence-not-index locating that lets a sparse/partial window revert correctly.
/// </summary>
public sealed class SharedReverseApplyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private static RecordEdit Edit(long seq, params FieldChange[] changes)
        => new(seq, T0.AddMinutes(seq), "u", changes, seq > 0);

    [Fact]
    public void In_memory_RevertTo_matches_the_shared_reverse_apply()
    {
        var history = new RecordEditHistory();
        var v0 = new Dictionary<string, object?> { ["a"] = 1L, ["b"] = "x" };
        var v1 = new Dictionary<string, object?> { ["a"] = 2L, ["b"] = "x" };
        var v2 = new Dictionary<string, object?> { ["a"] = 2L, ["b"] = "y" };
        history.Record(v0, v1, "u", T0);
        history.Record(v1, v2, "u", T0.AddMinutes(1));

        var viaInstance = history.RevertTo(v2, toSequence: 0);
        var viaShared = RecordEditHistory.ReverseApplyAfter(v2, history.Edits, toSequence: 0);

        Assert.Equal(viaInstance["a"], viaShared["a"]);
        Assert.Equal(viaInstance["b"], viaShared["b"]);
    }

    [Fact]
    public void Reverse_apply_locates_edits_by_sequence_not_list_position()
    {
        // A partial window: only the two most-recent edits (sequences 5 and 6) are
        // loaded, NOT a dense [0..6]. Reverting "to sequence 4" must still undo only
        // edits 5 and 6 — keying off Sequence, not index 0/1.
        var window = new List<RecordEdit>
        {
            Edit(5, new FieldChange("status", "open", "closed")),
            Edit(6, new FieldChange("priority", 1L, 3L)),
        };

        var current = new Dictionary<string, object?> { ["status"] = "closed", ["priority"] = 3L };
        var reverted = RecordEditHistory.ReverseApplyAfter(current, window, toSequence: 4);

        Assert.Equal("open", reverted["status"]);
        Assert.Equal(1L, reverted["priority"]);
    }

    [Fact]
    public void Reverse_apply_is_order_independent_over_the_edit_list()
    {
        // Out-of-order edits in the list must produce the same result as in-order:
        // Sequence is authoritative, so the highest sequence is reversed first.
        var inOrder = new List<RecordEdit>
        {
            Edit(0, new FieldChange("a", "1", "2")),
            Edit(1, new FieldChange("a", "2", "3")),
        };
        var shuffled = new List<RecordEdit> { inOrder[1], inOrder[0] };
        var current = new Dictionary<string, object?> { ["a"] = "3" };

        var a = RecordEditHistory.ReverseApplyAfter(current, inOrder, toSequence: -1);
        var b = RecordEditHistory.ReverseApplyAfter(current, shuffled, toSequence: -1);

        Assert.Equal("1", a["a"]);
        Assert.Equal("1", b["a"]);
    }
}
