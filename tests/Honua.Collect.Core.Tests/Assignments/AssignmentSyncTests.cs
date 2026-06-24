using Honua.Collect.Core.Assignments;
using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Assignments;

public sealed class AssignmentSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static AssignmentSnapshot Snapshot(string id, string user, AssignmentStatus status = AssignmentStatus.Assigned)
        => new()
        {
            AssignmentId = id,
            FormId = "f",
            AssignedToUserId = user,
            Title = "Pulled job",
            CreatedAtUtc = Now,
            Status = status,
        };

    private static IAuthSessionStore SessionFor(string userId)
    {
        var store = new AuthSessionStore();
        store.Set(new AuthSession { UserId = userId, AccessToken = "t", ExpiresAtUtc = Now.AddHours(1) });
        return store;
    }

    private static AssignmentService Service(
        IAssignmentStore store, IAssignmentSyncClient sync, string operatorId = "op-1")
        => new(store, SessionFor(operatorId), sync, new FakeClock(Now));

    [Fact]
    public async Task Pull_persists_new_server_assignments_into_the_inbox()
    {
        var store = new InMemoryAssignmentStore();
        var sync = new FakeAssignmentSyncClient();
        sync.Seed(Snapshot("s1", "op-1"), Snapshot("s2", "op-1"), Snapshot("other", "op-2"));
        var service = Service(store, sync);

        var inbox = await service.PullAsync();

        Assert.Equal(2, inbox.All.Count); // op-2's assignment isn't pulled for op-1
        Assert.Equal(2, (await store.LoadForUserAsync("op-1")).Count);
    }

    [Fact]
    public async Task Pull_does_not_clobber_a_locally_advanced_assignment()
    {
        // Operator already started s1 locally.
        var local = new FieldAssignment
        {
            AssignmentId = "s1", FormId = "f", AssignedToUserId = "op-1", Title = "Pulled job", CreatedAtUtc = Now,
        };
        local.Start("rec-local");
        var store = new InMemoryAssignmentStore(local);

        var sync = new FakeAssignmentSyncClient();
        sync.Seed(Snapshot("s1", "op-1")); // server still says Assigned
        var service = Service(store, sync);

        await service.PullAsync();

        var reloaded = (await store.LoadForUserAsync("op-1")).Single();
        Assert.Equal(AssignmentStatus.InProgress, reloaded.Status);
        Assert.Equal("rec-local", reloaded.RecordId);
    }

    [Fact]
    public async Task Pull_round_trips_an_advanced_server_status()
    {
        var store = new InMemoryAssignmentStore();
        var sync = new FakeAssignmentSyncClient();
        sync.Seed(Snapshot("s1", "op-1", AssignmentStatus.Reassigned));
        var service = Service(store, sync);

        await service.PullAsync();

        var pulled = (await store.LoadAllAsync()).Single();
        Assert.Equal(AssignmentStatus.Reassigned, pulled.Status);
        Assert.False(pulled.IsOpen);
    }

    [Fact]
    public async Task Push_reports_acted_on_assignments_back_to_the_dispatcher()
    {
        var untouched = new FieldAssignment
        {
            AssignmentId = "a0", FormId = "f", AssignedToUserId = "op-1", Title = "Untouched", CreatedAtUtc = Now,
        };
        var started = new FieldAssignment
        {
            AssignmentId = "a1", FormId = "f", AssignedToUserId = "op-1", Title = "Started", CreatedAtUtc = Now,
        };
        started.Start("rec-1");

        var store = new InMemoryAssignmentStore(untouched, started);
        var sync = new FakeAssignmentSyncClient();
        var service = Service(store, sync);

        var pushed = await service.PushStatusAsync();

        Assert.Equal(1, pushed); // only the started one (Assigned is skipped by default)
        var update = Assert.Single(sync.PushedUpdates);
        Assert.Equal("a1", update.AssignmentId);
        Assert.Equal(AssignmentStatus.InProgress, update.Status);
        Assert.Equal("rec-1", update.RecordId);
    }

    [Fact]
    public async Task Push_with_no_changes_does_not_call_the_server()
    {
        var store = new InMemoryAssignmentStore(new FieldAssignment
        {
            AssignmentId = "a0", FormId = "f", AssignedToUserId = "op-1", Title = "Assigned", CreatedAtUtc = Now,
        });
        var sync = new FakeAssignmentSyncClient();
        var service = Service(store, sync);

        var pushed = await service.PushStatusAsync();

        Assert.Equal(0, pushed);
        Assert.Equal(0, sync.PushCallCount);
    }

    [Fact]
    public async Task Pull_without_a_sync_client_throws()
    {
        var service = new AssignmentService(new InMemoryAssignmentStore(), SessionFor("op-1"), sync: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PullAsync());
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
