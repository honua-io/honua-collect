using Honua.Collect.Core.History;

namespace Honua.Collect.Core.Tests.History;

/// <summary>
/// Pins the dominant #38 bug shut: the durable history must persist each value
/// PRESERVING its type, not flatten it to text. A long stays a long, a double a
/// double, a bool a bool, a date string a string, and an empty/missing value stays
/// missing — so a revert restores the original typed value and re-diffing sees no
/// spurious type-flip change.
/// </summary>
public sealed class DurableHistoryTypeFidelityTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.GetTempFileName();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private SqliteRecordHistoryStore NewStore() => new($"Data Source={_dbPath}");

    private static RecordEdit Edit(long seq, string field, object? oldValue, object? newValue)
        => new(seq, T0.AddMinutes(seq), "u", [new FieldChange(field, oldValue, newValue)], seq > 0);

    [Theory]
    [InlineData(5L)]
    [InlineData(21.5d)]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData("a string")]
    public async Task Round_trips_a_scalar_value_with_its_type(object original)
    {
        var store = NewStore();
        await store.AppendAsync("r1", Edit(0, "f", null, original));

        var history = await store.GetHistoryAsync("r1");
        var change = history[0].Changes[0];

        // The persisted-then-loaded value equals the original AND keeps its CLR type
        // (the type-degradation bug returned the string "5" for 5L).
        Assert.Equal(original, change.NewValue);
        Assert.Equal(original.GetType(), change.NewValue!.GetType());
    }

    [Fact]
    public async Task A_missing_old_value_round_trips_as_null_so_revert_removes_the_field()
    {
        var store = NewStore();
        // Newly-set field: OldValue is null/missing.
        await store.AppendAsync("r1", Edit(0, "f", null, 7L));

        var history = await store.GetHistoryAsync("r1");
        Assert.Null(history[0].Changes[0].OldValue);

        // Reverse-applying removes the field (set-vs-remove keys off IsMissing, not
        // OldValue-is-null-only, so this matches the in-memory ReverseApply).
        var reverted = RecordEditHistory.ReverseApplyAfter(
            new Dictionary<string, object?> { ["f"] = 7L }, history, toSequence: -1);
        Assert.False(reverted.ContainsKey("f"));
    }

    [Fact]
    public async Task A_long_old_value_reverts_to_a_long_via_shared_reverse_apply()
    {
        var store = NewStore();
        await store.AppendAsync("r1", Edit(0, "count", 5L, 9L));

        var history = await store.GetHistoryAsync("r1");
        var reverted = RecordEditHistory.ReverseApplyAfter(
            new Dictionary<string, object?> { ["count"] = 9L }, history, toSequence: -1);

        Assert.IsType<long>(reverted["count"]);
        Assert.Equal(5L, reverted["count"]);
    }

    [Fact]
    public async Task An_empty_string_old_value_reverts_by_removing_the_field()
    {
        // IsMissing treats an empty string as missing, so reverse-apply must remove
        // the field rather than set it to "" — diverging here was a known prior bug.
        var store = NewStore();
        await store.AppendAsync("r1", Edit(0, "note", "", "filled in"));

        var history = await store.GetHistoryAsync("r1");
        var reverted = RecordEditHistory.ReverseApplyAfter(
            new Dictionary<string, object?> { ["note"] = "filled in" }, history, toSequence: -1);

        Assert.False(reverted.ContainsKey("note"));
    }
}
