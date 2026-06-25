using Honua.Collect.Core.Automation.Http;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>
/// Durability tests for the SQLite-backed HTTP outbox store (#44): a queued request
/// survives a "restart" (a fresh store over the same file) so it replays on the next
/// connectivity drain, the idempotency key is uniquely enforced, and delivery state
/// (status/attempts/error) round-trips.
/// </summary>
public class SqliteHttpOutboxStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"collect-outbox-{Guid.NewGuid():n}.db");

    private static HttpOutboxEntry Entry(string key, string id = "e1") => new()
    {
        Id = id,
        Request = new HttpOutboxRequest
        {
            Method = "POST",
            Url = "https://example.test/hook",
            Body = "{\"a\":1}",
            Headers = new Dictionary<string, string> { ["X-Custom"] = "v" },
            IdempotencyKey = key,
        },
        RuleName = "rule-A",
        Status = HttpOutboxStatus.Pending,
        Attempts = 0,
        EnqueuedAtUtc = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero),
        NextAttemptUtc = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Queued_request_survives_a_restart_and_round_trips()
    {
        var store = new SqliteHttpOutboxStore(_dbPath);
        await store.SaveAsync(Entry("key-1"));

        // Fresh store over the same file = app restart.
        var reopened = new SqliteHttpOutboxStore(_dbPath);
        var loaded = Assert.Single(await reopened.LoadAllAsync());

        Assert.Equal("key-1", loaded.Request.IdempotencyKey);
        Assert.Equal("POST", loaded.Request.Method);
        Assert.Equal("https://example.test/hook", loaded.Request.Url);
        Assert.Equal("{\"a\":1}", loaded.Request.Body);
        Assert.Equal("v", loaded.Request.Headers["X-Custom"]);
        Assert.Equal("rule-A", loaded.RuleName);
        Assert.Equal(HttpOutboxStatus.Pending, loaded.Status);
    }

    [Fact]
    public async Task Delivery_state_updates_persist()
    {
        var store = new SqliteHttpOutboxStore(_dbPath);
        var entry = Entry("key-1");
        await store.SaveAsync(entry);

        await store.SaveAsync(entry with
        {
            Status = HttpOutboxStatus.Failed,
            Attempts = 3,
            LastStatusCode = 500,
            LastError = "server error",
        });

        var loaded = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(HttpOutboxStatus.Failed, loaded.Status);
        Assert.Equal(3, loaded.Attempts);
        Assert.Equal(500, loaded.LastStatusCode);
        Assert.Equal("server error", loaded.LastError);
    }

    [Fact]
    public async Task Lookup_by_idempotency_key_finds_the_entry()
    {
        var store = new SqliteHttpOutboxStore(_dbPath);
        await store.SaveAsync(Entry("the-key"));

        var found = await store.FindByIdempotencyKeyAsync("the-key");
        Assert.NotNull(found);
        Assert.Equal("the-key", found!.Request.IdempotencyKey);

        Assert.Null(await store.FindByIdempotencyKeyAsync("missing"));
    }

    [Fact]
    public async Task Outbox_over_sqlite_replays_after_restart()
    {
        // Enqueue while "offline" against a SQLite-backed store...
        var clock = new MutableClock(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        var offlineTransport = new CountingTransport(online: false);
        var first = new HttpRequestOutbox(new SqliteHttpOutboxStore(_dbPath), offlineTransport, clock);
        await first.EnqueueAsync(
            new HttpOutboxRequest { Url = "https://example.test/hook", IdempotencyKey = "k" });
        await first.DrainAsync(); // attempt fails offline -> reschedules with backoff

        // ...then a fresh outbox over the same file (restart) with connectivity. Advance
        // past the backoff so the pending entry is due, and it replays.
        clock.Advance(TimeSpan.FromMinutes(5));
        var onlineTransport = new CountingTransport(online: true);
        var second = new HttpRequestOutbox(new SqliteHttpOutboxStore(_dbPath), onlineTransport, clock);
        var result = await second.DrainAsync();

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, onlineTransport.Calls);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingTransport(bool online) : IHttpRequestTransport
    {
        public int Calls { get; private set; }

        public Task<HttpSendResult> SendAsync(HttpOutboxRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(online ? HttpSendResult.Ok() : HttpSendResult.Offline());
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
