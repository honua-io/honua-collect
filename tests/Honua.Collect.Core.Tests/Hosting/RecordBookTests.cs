using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Hosting;

public class RecordBookTests
{
    private sealed class FakeStore : IRecordStore
    {
        public List<CollectRecordEntry> Saved { get; } = [];
        public List<CollectRecordEntry> Seed { get; } = [];
        public int LoadCalls { get; private set; }

        public Task SaveAsync(CollectRecordEntry entry, CancellationToken ct = default)
        {
            Saved.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CollectRecordEntry>> LoadAllAsync(CancellationToken ct = default)
        {
            LoadCalls++;
            return Task.FromResult<IReadOnlyList<CollectRecordEntry>>(Seed);
        }

        public Task DeleteAsync(string recordId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static CollectRecordEntry Entry(string id, DateTimeOffset created)
        => new(new FieldRecord { RecordId = id, FormId = "f", CreatedAtUtc = created, Status = RecordStatus.Submitted });

    [Fact]
    public async Task Initialize_loads_newest_first()
    {
        var store = new FakeStore();
        store.Seed.Add(Entry("old", DateTimeOffset.UtcNow.AddHours(-2)));
        store.Seed.Add(Entry("new", DateTimeOffset.UtcNow));
        var book = new RecordBook(store);

        await book.InitializeAsync();

        Assert.Equal(["new", "old"], book.All.Select(e => e.Record.RecordId));
    }

    [Fact]
    public async Task Initialize_is_idempotent_under_concurrency()
    {
        var store = new FakeStore();
        store.Seed.Add(Entry("a", DateTimeOffset.UtcNow));
        var book = new RecordBook(store);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => book.InitializeAsync()));

        Assert.Equal(1, store.LoadCalls);
        Assert.Single(book.All);
    }

    [Fact]
    public async Task AddSubmitted_inserts_at_front_and_persists()
    {
        var store = new FakeStore();
        var book = new RecordBook(store);
        await book.InitializeAsync();

        var entry = await book.AddSubmittedAsync(new FieldRecord { RecordId = "r1", FormId = "f", Status = RecordStatus.Submitted });

        Assert.Equal("r1", book.All[0].Record.RecordId);
        Assert.Contains(entry, store.Saved);
        Assert.Equal(RecordSyncState.Pending, entry.SyncState);
    }

    [Fact]
    public async Task All_returns_an_isolated_snapshot()
    {
        var store = new FakeStore();
        var book = new RecordBook(store);
        await book.InitializeAsync();
        await book.AddSubmittedAsync(new FieldRecord { RecordId = "r1", FormId = "f", Status = RecordStatus.Submitted });

        var snapshot = book.All;
        await book.AddSubmittedAsync(new FieldRecord { RecordId = "r2", FormId = "f", Status = RecordStatus.Submitted });

        Assert.Single(snapshot); // snapshot not mutated by the later add
        Assert.Equal(2, book.All.Count);
    }
}
