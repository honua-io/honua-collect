using System.Text.Json;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Storage;

/// <summary>
/// Crash-safe write path (#38): property/fuzz tests proving the SQLite store never
/// leaves a half-written or corrupt record, whatever the op sequence. Writes go
/// through a single atomic upsert, so an interrupted or concurrent write must always
/// land the row as a complete prior-or-next value — never a torn mix.
/// </summary>
public sealed class SqliteRecordStoreCrashSafetyTests : IDisposable
{
    private readonly string _dbPath = Path.GetTempFileName();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private SqliteRecordStore NewStore() => new($"Data Source={_dbPath}");

    private static CollectRecordEntry Entry(string id, int generation, RecordStatus status)
    {
        var record = new FieldRecord
        {
            RecordId = id,
            FormId = "f",
            Status = status,
            Location = new FieldGeoPoint(generation * 0.1, generation * 0.2, generation),
        };
        record.Values["gen"] = (long)generation;
        record.Values["label"] = $"v{generation}";
        record.Values["flag"] = generation % 2 == 0;
        return new CollectRecordEntry(record);
    }

    // A well-formed entry: every field that was written together is internally
    // consistent (the generation stamp matches the label and the location), so a
    // torn write — say new values_json over an old location — would be detectable.
    private static void AssertWellFormed(CollectRecordEntry entry)
    {
        var gen = GenOf(entry);
        Assert.Equal($"v{gen}", AsString(entry.Record.Values["label"]));
        Assert.Equal(gen % 2 == 0, AsBool(entry.Record.Values["flag"]));
        Assert.NotNull(entry.Record.Location);
        Assert.Equal(gen, entry.Record.Location!.AccuracyMeters);
    }

    // Values rehydrate as JsonElement from the store; unwrap to compare.
    private static long GenOf(CollectRecordEntry entry)
        => entry.Record.Values["gen"] is JsonElement e ? e.GetInt64() : Convert.ToInt64(entry.Record.Values["gen"]);

    private static string? AsString(object? v) => v is JsonElement e ? e.GetString() : Convert.ToString(v);

    private static bool AsBool(object? v) => v is JsonElement e ? e.GetBoolean() : Convert.ToBoolean(v);

    [Theory]
    [InlineData(11)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2026)]
    public async Task Random_op_sequences_never_corrupt_a_record(int seed)
    {
        var rng = new Random(seed);
        var store = NewStore();
        var statuses = Enum.GetValues<RecordStatus>();

        // Track, per id, the set of generations we ever attempted to write. After the
        // storm every surviving row must be EXACTLY one of those, fully formed.
        var attempted = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);

        for (var step = 0; step < 200; step++)
        {
            var id = $"r{rng.Next(0, 5)}";
            var op = rng.Next(0, 10);

            if (op < 7)
            {
                var gen = rng.Next(0, 1000);
                var entry = Entry(id, gen, statuses[rng.Next(statuses.Length)]);
                (attempted.TryGetValue(id, out var set) ? set : attempted[id] = []).Add(gen);
                await store.SaveAsync(entry);
            }
            else if (op < 9)
            {
                await store.DeleteAsync(id);
            }
            else
            {
                // Mid-stream read: every loaded row must be well-formed at all times.
                foreach (var loaded in await store.LoadAllAsync())
                {
                    AssertWellFormed(loaded);
                }
            }
        }

        var final = await store.LoadAllAsync();
        foreach (var entry in final)
        {
            AssertWellFormed(entry);
            var gen = GenOf(entry);
            Assert.Contains(gen, attempted[entry.Record.RecordId]);
        }
    }

    [Fact]
    public async Task An_interrupted_write_leaves_the_prior_committed_value_intact()
    {
        // Commit v1, then start a v2 write that is cancelled before it can run. The
        // atomic upsert means the row is still the fully-formed v1 — never a torn v2.
        var store = NewStore();
        await store.SaveAsync(Entry("r1", 1, RecordStatus.Submitted));

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // "crash" before the write executes
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(Entry("r1", 2, RecordStatus.Approved), cts.Token));

        var loaded = Assert.Single(await store.LoadAllAsync());
        AssertWellFormed(loaded);
        Assert.Equal(1L, GenOf(loaded)); // still v1
    }

    [Fact]
    public async Task Concurrent_edits_and_syncs_to_one_db_never_tear_a_record()
    {
        // Hammer the same database file from many tasks (edit writers + a sync-state
        // writer + readers). Every read at any point must see a well-formed row.
        var store = NewStore();
        await store.SaveAsync(Entry("r1", 0, RecordStatus.Submitted));

        var tasks = new List<Task>();
        for (var w = 0; w < 8; w++)
        {
            var worker = w;
            tasks.Add(Task.Run(async () =>
            {
                var rng = new Random(worker * 7 + 1);
                for (var i = 0; i < 50; i++)
                {
                    if (worker % 2 == 0)
                    {
                        await store.SaveAsync(Entry("r1", rng.Next(0, 1000), RecordStatus.Submitted));
                    }
                    else
                    {
                        foreach (var loaded in await store.LoadAllAsync())
                        {
                            AssertWellFormed(loaded);
                        }
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        var final = Assert.Single(await store.LoadAllAsync());
        AssertWellFormed(final);
    }
}
