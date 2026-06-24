using Honua.Collect.Core.History;
using Honua.Collect.Core.Records;

namespace Honua.Collect.Core.Tests.History;

/// <summary>
/// Durable offline edit history (BACKLOG #38): append-only, monotonic, ordered,
/// and surviving a "restart" (a fresh store over the same database file).
/// </summary>
public sealed class SqliteRecordHistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;

    public SqliteRecordHistoryStoreTests() => _dbPath = Path.GetTempFileName();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private SqliteRecordHistoryStore NewStore() => new($"Data Source={_dbPath}");

    private static RecordEdit Edit(long sequence, params (string Field, object? Old, object? New)[] changes)
        => new(
            sequence,
            T0.AddMinutes(sequence),
            "user-1",
            changes.Select(c => new FieldChange(c.Field, c.Old, c.New)).ToList(),
            sequence > 0);

    [Fact]
    public async Task Append_then_query_returns_the_edit_with_its_field_diff()
    {
        var store = NewStore();
        await store.AppendAsync("r1", Edit(0, ("status", "open", "closed")));

        var history = await store.GetHistoryAsync("r1");

        var edit = Assert.Single(history);
        Assert.Equal(0, edit.Sequence);
        Assert.Equal("user-1", edit.EditorUserId);
        var change = Assert.Single(edit.Changes);
        Assert.Equal("status", change.FieldId);
        Assert.Equal("open", change.OldValue);
        Assert.Equal("closed", change.NewValue);
    }

    [Fact]
    public async Task History_is_returned_in_ascending_version_order()
    {
        var store = NewStore();
        await store.AppendAsync("r1", Edit(0, ("a", "1", "2")));
        await store.AppendAsync("r1", Edit(1, ("a", "2", "3")));
        await store.AppendAsync("r1", Edit(2, ("a", "3", "4")));

        var history = await store.GetHistoryAsync("r1");

        Assert.Equal(new long[] { 0, 1, 2 }, history.Select(e => e.Sequence).ToArray());
        Assert.True(history[0].AfterSync is false);
        Assert.True(history[1].AfterSync);
    }

    [Fact]
    public async Task Next_sequence_tracks_the_count_of_appended_edits()
    {
        var store = NewStore();
        Assert.Equal(0, await store.GetNextSequenceAsync("r1"));

        await store.AppendAsync("r1", Edit(0, ("a", null, "1")));
        Assert.Equal(1, await store.GetNextSequenceAsync("r1"));

        await store.AppendAsync("r1", Edit(1, ("a", "1", "2")));
        Assert.Equal(2, await store.GetNextSequenceAsync("r1"));
    }

    [Fact]
    public async Task Out_of_order_or_duplicate_sequence_is_rejected()
    {
        var store = NewStore();
        await store.AppendAsync("r1", Edit(0, ("a", null, "1")));

        // A gap (skipping sequence 1) breaks the monotonic chain.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync("r1", Edit(2, ("a", "1", "2"))));

        // A duplicate of an already-stored sequence is rejected too.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync("r1", Edit(0, ("a", "1", "9"))));
    }

    [Fact]
    public async Task History_is_isolated_per_record()
    {
        var store = NewStore();
        await store.AppendAsync("r1", Edit(0, ("a", null, "1")));
        await store.AppendAsync("r2", Edit(0, ("b", null, "x")));

        Assert.Single(await store.GetHistoryAsync("r1"));
        Assert.Single(await store.GetHistoryAsync("r2"));
        Assert.Equal("b", (await store.GetHistoryAsync("r2"))[0].Changes[0].FieldId);
    }

    [Fact]
    public async Task History_survives_a_restart()
    {
        var first = NewStore();
        await first.AppendAsync("r1", Edit(0, ("status", "open", "closed")));
        await first.AppendAsync("r1", Edit(1, ("priority", "low", "high")));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // A brand-new store over the same file = an app restart.
        var second = NewStore();
        var history = await second.GetHistoryAsync("r1");

        Assert.Equal(2, history.Count);
        Assert.Equal("closed", history[0].Changes[0].NewValue);
        Assert.Equal("high", history[1].Changes[0].NewValue);
        // The next sequence continues from storage, not from zero.
        Assert.Equal(2, await second.GetNextSequenceAsync("r1"));
    }
}
