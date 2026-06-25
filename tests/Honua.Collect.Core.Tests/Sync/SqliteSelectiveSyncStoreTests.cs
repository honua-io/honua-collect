using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

public class SqliteSelectiveSyncStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"selsync-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task Round_trips_a_full_scope_config()
    {
        var store = new SqliteSelectiveSyncStore(_dbPath);
        var scope = new LayerSyncScope
        {
            LayerKey = "trees",
            Enabled = true,
            Areas = [new SyncAreaBounds(45, -123, 46, -122)],
            Extent = new SyncExtent(500_000, 4_100_000, 510_000, 4_110_000),
            Where = SyncAttributeFilter.Parse("status = 'open'"),
            DateWindow = new SyncDateWindow { MaxAge = TimeSpan.FromDays(7) },
            RecordIds = new HashSet<string>(["a", "b"], StringComparer.Ordinal),
        };

        await store.SaveAsync(scope);

        var loaded = Assert.Single(await store.LoadAllAsync());
        Assert.Equal("trees", loaded.LayerKey);
        Assert.True(loaded.Enabled);
        Assert.Equal(45, Assert.Single(loaded.Areas).MinLatitude);
        Assert.NotNull(loaded.Extent);
        Assert.Equal(500_000, loaded.Extent!.MinX);
        Assert.Equal(510_000, loaded.Extent.MaxX);
        Assert.Equal("status = 'open'", loaded.Where!.Where);
        Assert.Equal(TimeSpan.FromDays(7), loaded.DateWindow!.MaxAge);
        Assert.Equal(2, loaded.RecordIds!.Count);
        Assert.Contains("a", loaded.RecordIds);
    }

    [Fact]
    public async Task Reloaded_where_filter_still_evaluates()
    {
        var store = new SqliteSelectiveSyncStore(_dbPath);
        await store.SaveAsync(new LayerSyncScope
        {
            LayerKey = "trees",
            Where = SyncAttributeFilter.Parse("priority >= 5"),
        });

        var loaded = Assert.Single(await store.LoadAllAsync());
        var record = new FieldRecord { RecordId = "r", FormId = "f" };
        record.Values["priority"] = 9;

        Assert.True(loaded.Where!.Matches(record)); // recompiled predicate runs
    }

    [Fact]
    public async Task Load_plan_assembles_scopes_into_a_working_plan()
    {
        ISelectiveSyncStore store = new SqliteSelectiveSyncStore(_dbPath);
        await store.SaveAsync(new LayerSyncScope { LayerKey = "trees" });
        await store.SaveAsync(new LayerSyncScope { LayerKey = "roads", Enabled = false });

        var plan = await store.LoadPlanAsync();

        Assert.True(plan.IncludesLayer("trees"));
        Assert.False(plan.IncludesLayer("roads"));
        Assert.False(plan.IncludesLayer("unlisted")); // opt-in by default
    }

    [Fact]
    public async Task Save_is_upsert_and_delete_reverts_to_full_sync()
    {
        var store = new SqliteSelectiveSyncStore(_dbPath);
        await store.SaveAsync(new LayerSyncScope { LayerKey = "trees", Enabled = true });
        await store.SaveAsync(new LayerSyncScope { LayerKey = "trees", Enabled = false }); // overwrite

        Assert.False(Assert.Single(await store.LoadAllAsync()).Enabled);

        await store.DeleteAsync("trees");
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Minimal_scope_round_trips_with_defaults()
    {
        var store = new SqliteSelectiveSyncStore(_dbPath);
        await store.SaveAsync(new LayerSyncScope { LayerKey = "trees" });

        var loaded = Assert.Single(await store.LoadAllAsync());
        Assert.True(loaded.Enabled);
        Assert.Empty(loaded.Areas);
        Assert.Null(loaded.Extent);
        Assert.Null(loaded.Where);
        Assert.Null(loaded.DateWindow);
        Assert.Null(loaded.RecordIds);
        Assert.False(loaded.HasRecordFilter);
    }
}
