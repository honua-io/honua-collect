using Honua.Collect.Core.History;
using Honua.Collect.Presentation.History;

namespace Honua.Collect.Presentation.Tests;

/// <summary>
/// The record edit-history view-model (BACKLOG #38) lists durable versions for a
/// record, newest first, with a readable per-version header and field changes.
/// </summary>
public sealed class RecordHistoryViewModelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly SqliteRecordHistoryStore _store;

    public RecordHistoryViewModelTests()
    {
        _dbPath = Path.GetTempFileName();
        _store = new SqliteRecordHistoryStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static RecordEdit Edit(long sequence, bool afterSync, params (string Field, object? Old, object? New)[] changes)
        => new(
            sequence,
            T0.AddMinutes(sequence),
            "soleil",
            changes.Select(c => new FieldChange(c.Field, c.Old, c.New)).ToList(),
            afterSync);

    [Fact]
    public async Task Loads_versions_newest_first_with_one_based_numbering()
    {
        await _store.AppendAsync("r1", Edit(0, afterSync: false, ("status", "open", "closed")));
        await _store.AppendAsync("r1", Edit(1, afterSync: true, ("count", 1, 2)));

        var vm = new RecordHistoryViewModel(_store, "r1");
        await vm.LoadAsync();

        Assert.True(vm.HasHistory);
        Assert.Equal(2, vm.Versions.Count);
        // Newest first: sequence 1 -> version 2.
        Assert.Equal(2, vm.Versions[0].Version);
        Assert.True(vm.Versions[0].AfterSync);
        Assert.Equal(1, vm.Versions[1].Version);

        var topChange = Assert.Single(vm.Versions[0].Changes);
        Assert.Equal("count", topChange.FieldId);
        Assert.Equal("1", topChange.BeforeText);
        Assert.Equal("2", topChange.AfterText);
        Assert.Contains("v2", vm.Versions[0].Header);
        Assert.Contains("post-sync", vm.Versions[0].Header);
    }

    [Fact]
    public async Task Empty_history_reports_no_versions()
    {
        var vm = new RecordHistoryViewModel(_store, "ghost");
        await vm.LoadAsync();

        Assert.False(vm.HasHistory);
        Assert.Empty(vm.Versions);
    }
}
