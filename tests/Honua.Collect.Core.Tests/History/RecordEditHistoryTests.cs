using Honua.Collect.Core.History;

namespace Honua.Collect.Core.Tests.History;

public class RecordEditHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, object?> Values(params (string Id, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void Records_a_field_level_diff_of_who_what_when()
    {
        var history = new RecordEditHistory();
        var before = Values(("status", "open"), ("notes", "first"));
        var after = Values(("status", "closed"), ("notes", "first"));

        var edit = history.Record(before, after, "user-1", T0, afterSync: false, note: "triage");

        Assert.NotNull(edit);
        Assert.Equal(0, edit!.Sequence);
        Assert.Equal("user-1", edit.EditorUserId);
        Assert.Equal("triage", edit.Note);
        var change = Assert.Single(edit.Changes);
        Assert.Equal("status", change.FieldId);
        Assert.Equal("open", change.OldValue);
        Assert.Equal("closed", change.NewValue);
    }

    [Fact]
    public void A_no_op_edit_is_not_recorded()
    {
        var history = new RecordEditHistory();
        var values = Values(("a", "1"), ("b", null), ("c", new List<object?>()));

        // Same values, plus null-vs-missing and empty-list-vs-missing — all "missing".
        var equivalent = Values(("a", "1"));

        Assert.Null(history.Record(values, equivalent, "u", T0));
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Post_sync_edits_are_flagged_and_not_blocked()
    {
        var history = new RecordEditHistory();
        history.Record(Values(("x", "1")), Values(("x", "2")), "u", T0); // pre-sync
        history.Record(Values(("x", "2")), Values(("x", "3")), "u", T0.AddHours(1), afterSync: true);

        Assert.Equal(2, history.Count);
        Assert.True(history.HasPostSyncEdits);
        Assert.True(history.Last!.AfterSync);
    }

    [Fact]
    public void UndoLast_reconstructs_the_values_before_the_last_edit()
    {
        var history = new RecordEditHistory();
        var v0 = Values(("status", "open"));
        var v1 = Values(("status", "closed"));
        history.Record(v0, v1, "u", T0);

        var reverted = history.UndoLast(v1);

        Assert.Equal("open", reverted["status"]);
    }

    [Fact]
    public void RevertTo_reconstructs_an_earlier_version_across_multiple_edits()
    {
        var history = new RecordEditHistory();
        var v0 = Values(("status", "open"), ("priority", "low"));
        var v1 = Values(("status", "closed"), ("priority", "low"));
        var v2 = Values(("status", "closed"), ("priority", "high"));
        history.Record(v0, v1, "u", T0);            // seq 0: status open→closed
        history.Record(v1, v2, "u", T0.AddDays(1)); // seq 1: priority low→high

        // Revert to just after seq 0 (undo only the priority change).
        var atSeq0 = history.RevertTo(v2, toSequence: 0);
        Assert.Equal("closed", atSeq0["status"]);
        Assert.Equal("low", atSeq0["priority"]);

        // Revert all the way to the original.
        var original = history.RevertTo(v2, toSequence: -1);
        Assert.Equal("open", original["status"]);
        Assert.Equal("low", original["priority"]);
    }

    [Fact]
    public void Reverting_a_newly_added_field_removes_it()
    {
        var history = new RecordEditHistory();
        var v0 = Values(("a", "1"));
        var v1 = Values(("a", "1"), ("b", "new")); // b added
        history.Record(v0, v1, "u", T0);

        var reverted = history.UndoLast(v1);

        Assert.False(reverted.ContainsKey("b"));
        Assert.Equal("1", reverted["a"]);
    }

    [Fact]
    public void Does_not_mutate_the_supplied_values()
    {
        var history = new RecordEditHistory();
        var v1 = Values(("status", "closed"));
        history.Record(Values(("status", "open")), v1, "u", T0);

        history.UndoLast(v1);

        Assert.Equal("closed", v1["status"]); // input untouched
    }

    [Fact]
    public void List_values_compare_by_contents()
    {
        var history = new RecordEditHistory();
        var before = Values(("tags", new List<object?> { "a", "b" }));
        var sameContents = Values(("tags", new List<object?> { "a", "b" }));
        var changed = Values(("tags", new List<object?> { "a", "c" }));

        Assert.Null(history.Record(before, sameContents, "u", T0)); // equal contents → no edit
        Assert.NotNull(history.Record(before, changed, "u", T0));   // different contents → edit
    }

    [Fact]
    public void Record_guards_arguments()
    {
        var history = new RecordEditHistory();
        var values = Values(("a", "1"));
        Assert.Throws<ArgumentNullException>(() => history.Record(null!, values, "u", T0));
        Assert.Throws<ArgumentNullException>(() => history.Record(values, null!, "u", T0));
        Assert.Throws<ArgumentException>(() => history.Record(values, values, "  ", T0));
    }
}
