using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Enterprise;

public class AuditTrailTests
{
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public FakeTimeProvider()
            : this(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
        {
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static IAuditSink AsSink(AuditTrail trail) => trail;

    [Fact]
    public async Task Events_are_recorded_in_order_with_monotonic_contiguous_sequence()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var trail = new AuditTrail(new InMemoryAuditStore(), clock);
        var sink = AsSink(trail);

        sink.SignIn("u1");
        clock.Advance(TimeSpan.FromSeconds(1));
        sink.Record(new AuditEvent(clock.GetUtcNow(), "u1", AuditAction.RecordCreated, "r1"));
        clock.Advance(TimeSpan.FromSeconds(1));
        sink.SignOut("u1");

        var entries = await trail.QueryAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal(new long[] { 0, 1, 2 }, entries.Select(e => e.Sequence).ToArray());
        Assert.Equal(AuditAction.SignIn, entries[0].Event.Action);
        Assert.Equal(AuditAction.RecordCreated, entries[1].Event.Action);
        Assert.Equal(AuditAction.SignOut, entries[2].Event.Action);
        // Chain links: each previous-hash points at the prior hash.
        Assert.Equal(string.Empty, entries[0].PreviousHash);
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
        Assert.Equal(entries[1].Hash, entries[2].PreviousHash);
    }

    [Fact]
    public async Task Chain_verifies_and_a_tampered_entry_is_detected()
    {
        var store = new InMemoryAuditStore();
        var trail = new AuditTrail(store, new FakeTimeProvider());
        AsSink(trail).SignIn("u1");
        AsSink(trail).SyncPushed("u1", 5);

        Assert.True(await trail.VerifyAsync());

        // Re-create a trail over a store seeded with a forged middle entry.
        var forgedStore = new InMemoryAuditStore();
        var good = await trail.QueryAsync();
        await forgedStore.AppendAsync(good[0]);
        var tampered = good[1] with { Event = good[1].Event with { Details = "count=999" } };
        await forgedStore.AppendAsync(tampered);

        var forgedTrail = new AuditTrail(forgedStore);
        Assert.False(await forgedTrail.VerifyAsync());
    }

    [Fact]
    public async Task Secrets_are_scrubbed_from_logged_details()
    {
        var trail = new AuditTrail(new InMemoryAuditStore());
        AsSink(trail).Record(new AuditEvent(
            DateTimeOffset.UtcNow, "u1", AuditAction.SignIn,
            Details: "login token=abc123secret password=hunter2 Authorization: Bearer eyJabc.def.ghi"));

        var entry = Assert.Single(await trail.QueryAsync());
        var details = entry.Event.Details!;
        Assert.DoesNotContain("abc123secret", details);
        Assert.DoesNotContain("hunter2", details);
        Assert.DoesNotContain("eyJabc.def.ghi", details);
        Assert.Contains(SecretScrubber.Redacted, details);
    }

    [Fact]
    public async Task Query_filters_by_user_action_and_time_window()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var trail = new AuditTrail(new InMemoryAuditStore(), clock);
        var sink = AsSink(trail);

        sink.SignIn("alice");
        clock.Advance(TimeSpan.FromMinutes(1));
        sink.Record(new AuditEvent(clock.GetUtcNow(), "bob", AuditAction.RecordDeleted, "r9"));
        clock.Advance(TimeSpan.FromMinutes(1));
        sink.SignIn("alice");

        var aliceOnly = await trail.QueryAsync(new AuditQuery(UserId: "alice"));
        Assert.Equal(2, aliceOnly.Count);
        Assert.All(aliceOnly, e => Assert.Equal("alice", e.Event.UserId));

        var deletes = await trail.QueryAsync(new AuditQuery(Action: AuditAction.RecordDeleted));
        Assert.Equal("bob", Assert.Single(deletes).Event.UserId);

        var window = await trail.QueryAsync(new AuditQuery(
            SinceUtc: new DateTimeOffset(2026, 6, 1, 0, 0, 30, TimeSpan.Zero),
            UntilUtc: new DateTimeOffset(2026, 6, 1, 0, 1, 30, TimeSpan.Zero)));
        Assert.Equal(AuditAction.RecordDeleted, Assert.Single(window).Event.Action);
    }

    [Fact]
    public async Task Export_returns_the_trail_as_jsonl_with_chain_hashes()
    {
        var trail = new AuditTrail(new InMemoryAuditStore(), new FakeTimeProvider());
        AsSink(trail).SignIn("u1");
        AsSink(trail).SignOut("u1");

        var jsonl = await trail.ExportJsonlAsync();
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"seq\":0", lines[0]);
        Assert.Contains("\"action\":\"SignIn\"", lines[0]);
        Assert.Contains("\"hash\":", lines[0]);
        Assert.Contains("\"action\":\"SignOut\"", lines[1]);
    }

    [Fact]
    public async Task Sqlite_store_persists_the_trail_across_instances()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.db");
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
            var trail = new AuditTrail(new SqliteAuditStore(dbPath), clock);
            AsSink(trail).SignIn("u1", details: "token=should-not-persist");
            clock.Advance(TimeSpan.FromSeconds(1));
            AsSink(trail).SyncPulled("u1", 12);

            // A fresh store over the same file must see the persisted, ordered trail.
            var reopened = new AuditTrail(new SqliteAuditStore(dbPath));
            var entries = await reopened.QueryAsync();

            Assert.Equal(2, entries.Count);
            Assert.Equal(new long[] { 0, 1 }, entries.Select(e => e.Sequence).ToArray());
            Assert.DoesNotContain("should-not-persist", entries[0].Event.Details);
            Assert.True(await reopened.VerifyAsync());

            // A continued trail keeps the sequence monotonic across instances.
            AsSink(reopened).SignOut("u1");
            var final = await reopened.QueryAsync();
            Assert.Equal(new long[] { 0, 1, 2 }, final.Select(e => e.Sequence).ToArray());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task Sqlite_store_rejects_a_duplicate_sequence()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteAuditStore(dbPath);
            var entry = new AuditEntry(0, new AuditEvent(DateTimeOffset.UtcNow, "u1", AuditAction.SignIn), string.Empty, "h0");
            await store.AppendAsync(entry);

            await Assert.ThrowsAnyAsync<Exception>(() => store.AppendAsync(entry));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
