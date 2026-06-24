using Honua.Collect.Core.Assignments;
using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Assignments;

public sealed class AssignmentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static FieldAssignment New(
        string id = "a1",
        string user = "op-1",
        DateTimeOffset? due = null,
        AssignmentPriority priority = AssignmentPriority.Normal) => new()
    {
        AssignmentId = id,
        FormId = "hydrant-survey",
        AssignedToUserId = user,
        Title = "Inspect hydrant",
        DueAtUtc = due,
        Priority = priority,
        CreatedAtUtc = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero),
    };

    private static IAuthSessionStore SessionFor(string? userId)
    {
        var store = new AuthSessionStore();
        if (userId is not null)
        {
            store.Set(new AuthSession
            {
                UserId = userId,
                AccessToken = "tok",
                ExpiresAtUtc = Now.AddHours(1),
            });
        }

        return store;
    }

    private static DevicePrincipal Dispatcher(string userId = "boss") =>
        new(userId, [CollectRole.Create("dispatcher", CollectPermission.ManageAssignments)]);

    private static AssignmentService Service(
        IAssignmentStore store,
        string? operatorId = "op-1",
        IAssignmentSyncClient? sync = null)
        => new(store, SessionFor(operatorId), sync, new FakeClock(Now));

    [Fact]
    public async Task Inbox_is_scoped_to_the_signed_in_operator()
    {
        var store = new InMemoryAssignmentStore(
            New("mine-1", "op-1"),
            New("mine-2", "op-1"),
            New("theirs", "op-2"));
        var service = Service(store, operatorId: "op-1");

        var inbox = await service.GetInboxAsync();

        Assert.Equal("op-1", inbox.UserId);
        Assert.Equal(2, inbox.All.Count);
        Assert.All(inbox.All, a => Assert.Equal("op-1", a.AssignedToUserId));
    }

    [Fact]
    public async Task Inbox_filters_by_status()
    {
        var done = New("done", "op-1");
        done.Start("rec");
        done.Complete();
        var store = new InMemoryAssignmentStore(New("open", "op-1"), done);
        var service = Service(store);

        var assignedOnly = await service.GetInboxAsync(AssignmentStatus.Assigned);
        Assert.Equal(["open"], assignedOnly.All.Select(a => a.AssignmentId));

        var completedOnly = await service.GetInboxAsync(AssignmentStatus.Completed);
        Assert.Equal(["done"], completedOnly.All.Select(a => a.AssignmentId));
    }

    [Fact]
    public async Task Only_the_assignee_can_act_on_an_assignment()
    {
        var store = new InMemoryAssignmentStore(New("a1", "op-2")); // belongs to someone else
        var service = Service(store, operatorId: "op-1");

        var ex = await Assert.ThrowsAsync<AssignmentAccessException>(() => service.AcceptAsync("a1"));
        Assert.Equal("op-1", ex.UserId);
        Assert.Equal("a1", ex.AssignmentId);
    }

    [Fact]
    public async Task Complete_before_start_is_rejected()
    {
        var store = new InMemoryAssignmentStore(New("a1", "op-1"));
        var service = Service(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync("a1"));
    }

    [Fact]
    public async Task Start_then_complete_links_the_record_and_persists()
    {
        var store = new InMemoryAssignmentStore(New("a1", "op-1"));
        var service = Service(store);

        await service.StartAsync("a1", "rec-42");
        var completed = await service.CompleteAsync("a1");

        Assert.Equal(AssignmentStatus.Completed, completed.Status);
        Assert.Equal("rec-42", completed.RecordId);

        // Persisted: a fresh inbox sees the completed, record-linked assignment.
        var reloaded = (await store.LoadForUserAsync("op-1")).Single();
        Assert.Equal(AssignmentStatus.Completed, reloaded.Status);
        Assert.Equal("rec-42", reloaded.RecordId);
        Assert.Equal(Now, reloaded.CompletedAtUtc);
    }

    [Fact]
    public async Task Dispatch_requires_the_manage_assignments_role()
    {
        var store = new InMemoryAssignmentStore();
        var service = Service(store);
        var unprivileged = new DevicePrincipal(
            "boss", [CollectRole.Create("worker", CollectPermission.CaptureRecords)]);

        await Assert.ThrowsAsync<PermissionDeniedException>(
            () => service.DispatchAsync(unprivileged, New("a1", "op-1")));

        // A dispatcher with the role succeeds and persists.
        await service.DispatchAsync(Dispatcher(), New("a1", "op-1"));
        Assert.Single(await store.LoadForUserAsync("op-1"));
    }

    [Fact]
    public async Task Reassign_requires_the_role_closes_original_and_moves_work()
    {
        var store = new InMemoryAssignmentStore(New("a1", "op-1"));
        var service = Service(store);

        var moved = await service.ReassignAsync(Dispatcher(), "a1", "op-2", "a1-r");

        Assert.Equal("op-2", moved.AssignedToUserId);
        Assert.Equal(AssignmentStatus.Assigned, moved.Status);

        var original = (await store.LoadForUserAsync("op-1")).Single();
        Assert.Equal(AssignmentStatus.Reassigned, original.Status);
        Assert.False(original.IsOpen);

        var reassigned = (await store.LoadForUserAsync("op-2")).Single();
        Assert.Equal("a1-r", reassigned.AssignmentId);
    }

    [Fact]
    public async Task Status_counts_group_the_operators_assignments()
    {
        var inProgress = New("ip", "op-1");
        inProgress.Start("rec");
        var store = new InMemoryAssignmentStore(New("open1", "op-1"), New("open2", "op-1"), inProgress);
        var service = Service(store);

        var counts = await service.GetStatusCountsAsync();

        Assert.Equal(2, counts[AssignmentStatus.Assigned]);
        Assert.Equal(1, counts[AssignmentStatus.InProgress]);
    }

    [Fact]
    public async Task Acting_while_signed_out_throws()
    {
        var store = new InMemoryAssignmentStore(New("a1", "op-1"));
        var service = Service(store, operatorId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetInboxAsync());
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
