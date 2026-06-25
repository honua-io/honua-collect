using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

/// <summary>
/// CRDT-style last-writer-with-history merge (#38): concurrent edits converge
/// deterministically, no field is lost, and the merge is commutative, idempotent, and
/// associative (so order of reconciliation never changes the result).
/// </summary>
public sealed class CrdtRecordMergeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, CrdtFieldVersion> Side(
        string actor,
        params (string Field, object? Value, int MinutesAfterT0)[] fields)
        => fields.ToDictionary(
            f => f.Field,
            f => new CrdtFieldVersion(f.Value, T0.AddMinutes(f.MinutesAfterT0), actor),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Later_write_wins_per_field()
    {
        var local = Side("A", ("name", "local", 0), ("count", 5L, 10));
        var server = Side("B", ("name", "server", 5), ("count", 9L, 2));

        var merged = CrdtRecordMerge.Merge(local, server);

        Assert.Equal("server", merged["name"].Value); // server wrote name later
        Assert.Equal(5L, merged["count"].Value);       // local wrote count later
    }

    [Fact]
    public void No_field_is_lost_when_each_side_only_touched_some_fields()
    {
        var local = Side("A", ("only_local", "L", 1));
        var server = Side("B", ("only_server", "S", 1));

        var merged = CrdtRecordMerge.Merge(local, server);

        Assert.Equal("L", merged["only_local"].Value);
        Assert.Equal("S", merged["only_server"].Value);
    }

    [Fact]
    public void Merge_is_commutative()
    {
        var a = Side("A", ("x", 1L, 3), ("y", "a", 1));
        var b = Side("B", ("x", 2L, 1), ("y", "b", 5));

        var ab = CrdtRecordMerge.Merge(a, b);
        var ba = CrdtRecordMerge.Merge(b, a);

        Assert.Equal(ab["x"].Value, ba["x"].Value);
        Assert.Equal(ab["y"].Value, ba["y"].Value);
    }

    [Fact]
    public void Merge_is_idempotent()
    {
        var a = Side("A", ("x", 1L, 3), ("y", "a", 1));

        var once = CrdtRecordMerge.Merge(a, a);

        Assert.Equal(1L, once["x"].Value);
        Assert.Equal("a", once["y"].Value);
    }

    [Fact]
    public void Merge_is_associative_across_three_replicas()
    {
        var a = Side("A", ("x", "a", 1));
        var b = Side("B", ("x", "b", 2));
        var c = Side("C", ("x", "c", 3));

        var left = CrdtRecordMerge.Merge(CrdtRecordMerge.Merge(a, b), c);
        var right = CrdtRecordMerge.Merge(a, CrdtRecordMerge.Merge(b, c));

        Assert.Equal("c", left["x"].Value);  // c is latest
        Assert.Equal(left["x"].Value, right["x"].Value);
    }

    [Fact]
    public void Exact_timestamp_tie_is_broken_deterministically_by_actor()
    {
        // Same instant: the higher actor id wins, regardless of argument order, so the
        // tie-break is reachable AND non-divergent.
        var a = Side("actor-A", ("x", "from-a", 0));
        var b = Side("actor-B", ("x", "from-b", 0));

        var ab = CrdtRecordMerge.Merge(a, b);
        var ba = CrdtRecordMerge.Merge(b, a);

        Assert.Equal("from-b", ab["x"].Value); // "actor-B" > "actor-A" ordinally
        Assert.Equal(ab["x"].Value, ba["x"].Value);
    }

    [Fact]
    public void A_later_clear_wins_over_an_earlier_value()
    {
        var local = Side("A", ("note", "had text", 1));
        var server = Side("B", ("note", null, 5)); // cleared later

        var merged = CrdtRecordMerge.Merge(local, server);
        var record = CrdtRecordMerge.ToRecord("r1", "f", merged);

        Assert.False(record.Values.ContainsKey("note")); // converged delete
    }

    [Fact]
    public void ToRecord_keeps_only_non_missing_winning_values()
    {
        var merged = CrdtRecordMerge.Merge(
            Side("A", ("kept", "v", 1)),
            Side("B", ("cleared", "", 1)));

        var record = CrdtRecordMerge.ToRecord("r1", "f", merged);

        Assert.Equal("v", record.Values["kept"]);
        Assert.False(record.Values.ContainsKey("cleared"));
    }
}
