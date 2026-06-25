using Honua.Collect.Core.Field.Related;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Related;

public sealed class SqliteRelatedRecordStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRelatedRecordStore _store;

    public SqliteRelatedRecordStoreTests()
    {
        _dbPath = Path.GetTempFileName();
        _store = new SqliteRelatedRecordStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static FieldRecordLinkValue Link(string id, string? label = null) => new()
    {
        RecordId = id,
        FormId = "deficiency",
        Label = label,
    };

    [Fact]
    public async Task Links_round_trip_in_insertion_order()
    {
        await _store.LinkAsync("p1", "deficiencies", Link("d1", "First"));
        await _store.LinkAsync("p1", "deficiencies", Link("d2", "Second"));

        var children = await _store.ListAsync("p1", "deficiencies");
        Assert.Equal(["d1", "d2"], children.Select(c => c.RecordId));
        Assert.Equal("First", children[0].Label);
        Assert.Equal("deficiency", children[0].FormId);
    }

    [Fact]
    public async Task Linking_the_same_child_twice_is_idempotent()
    {
        await _store.LinkAsync("p1", "deficiencies", Link("d1"));
        await _store.LinkAsync("p1", "deficiencies", Link("d1", "updated label"));

        var children = await _store.ListAsync("p1", "deficiencies");
        Assert.Single(children);
        Assert.Equal("updated label", children[0].Label);
    }

    [Fact]
    public async Task Unlink_removes_a_single_child()
    {
        await _store.LinkAsync("p1", "deficiencies", Link("d1"));
        await _store.LinkAsync("p1", "deficiencies", Link("d2"));

        Assert.True(await _store.UnlinkAsync("p1", "deficiencies", "d1"));
        Assert.False(await _store.UnlinkAsync("p1", "deficiencies", "missing"));

        var children = await _store.ListAsync("p1", "deficiencies");
        Assert.Equal(["d2"], children.Select(c => c.RecordId));
    }

    [Fact]
    public async Task Links_are_isolated_per_parent_and_field()
    {
        await _store.LinkAsync("p1", "deficiencies", Link("d1"));
        await _store.LinkAsync("p2", "deficiencies", Link("d2"));
        await _store.LinkAsync("p1", "attachments", Link("a1"));

        Assert.Equal(["d1"], (await _store.ListAsync("p1", "deficiencies")).Select(c => c.RecordId));
        Assert.Equal(["d2"], (await _store.ListAsync("p2", "deficiencies")).Select(c => c.RecordId));
        Assert.Equal(["a1"], (await _store.ListAsync("p1", "attachments")).Select(c => c.RecordId));
    }

    [Fact]
    public async Task Deleting_a_parent_cascades_and_returns_freed_children()
    {
        await _store.LinkAsync("p1", "deficiencies", Link("d1"), RecordLinkBehavior.Cascade);
        await _store.LinkAsync("p1", "deficiencies", Link("d2"), RecordLinkBehavior.Cascade);

        var freed = await _store.DeleteParentAsync("p1");

        Assert.Equal(["d1", "d2"], freed);
        Assert.Empty(await _store.ListAsync("p1", "deficiencies"));
    }

    [Fact]
    public async Task Deleting_a_parent_with_restrict_children_throws_and_keeps_links()
    {
        await _store.LinkAsync("p1", "deficiencies", Link("d1"), RecordLinkBehavior.Restrict);

        var ex = await Assert.ThrowsAsync<RelatedRecordIntegrityException>(
            () => _store.DeleteParentAsync("p1"));
        Assert.Equal("p1", ex.ParentRecordId);
        Assert.Equal(1, ex.ChildCount);

        // The refused delete left the link in place.
        Assert.Single(await _store.ListAsync("p1", "deficiencies"));
    }

    [Fact]
    public async Task Restrict_blocks_the_whole_delete_even_when_some_links_cascade()
    {
        await _store.LinkAsync("p1", "deficiencies", Link("d1"), RecordLinkBehavior.Cascade);
        await _store.LinkAsync("p1", "attachments", Link("a1"), RecordLinkBehavior.Restrict);

        await Assert.ThrowsAsync<RelatedRecordIntegrityException>(() => _store.DeleteParentAsync("p1"));

        // No partial cascade: the cascade link is still present too.
        Assert.Single(await _store.ListAsync("p1", "deficiencies"));
        Assert.Single(await _store.ListAsync("p1", "attachments"));
    }

    [Fact]
    public async Task Survives_a_new_store_over_the_same_file()
    {
        await _store.LinkAsync("p1", "deficiencies", Link("d1", "First"));

        var reopened = new SqliteRelatedRecordStore($"Data Source={_dbPath}");
        var children = await reopened.ListAsync("p1", "deficiencies");
        Assert.Equal(["d1"], children.Select(c => c.RecordId));
    }
}
