using Honua.Collect.Core.Assignments;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Assignments;

namespace Honua.Collect.Presentation.Tests;

public sealed class AssignmentInboxViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static FieldAssignment New(string id, string user = "op-1", DateTimeOffset? due = null) => new()
    {
        AssignmentId = id,
        FormId = "site-survey",
        AssignedToUserId = user,
        Title = $"Job {id}",
        DueAtUtc = due,
        CreatedAtUtc = Now.AddDays(-1),
    };

    private static AssignmentService Service(string operatorId, params FieldAssignment[] seed)
    {
        var sessions = new AuthSessionStore();
        sessions.Set(new AuthSession { UserId = operatorId, AccessToken = "t", ExpiresAtUtc = Now.AddHours(1) });
        return new AssignmentService(new MemStore(seed), sessions, sync: null, new FakeClock(Now));
    }

    private static AssignmentInboxViewModel Vm(AssignmentService service)
        => new(service, () => Now);

    [Fact]
    public async Task Refresh_loads_open_and_completed_rows_with_counts()
    {
        var completed = New("done");
        completed.Start("rec");
        completed.Complete();
        var service = Service("op-1", New("open", due: Now.AddDays(2)), New("overdue", due: Now.AddDays(-1)), completed);
        var vm = Vm(service);

        await vm.RefreshAsync();

        Assert.Equal(["overdue", "open"], vm.OpenRows.Select(r => r.AssignmentId)); // overdue (soonest due) first
        Assert.Equal(["done"], vm.CompletedRows.Select(r => r.AssignmentId));
        Assert.Equal(2, vm.OpenCount);
        Assert.Equal(1, vm.OverdueCount);
        Assert.Equal("2 open · 1 overdue", vm.Header);
    }

    [Fact]
    public async Task Accept_then_start_then_complete_drives_the_lifecycle()
    {
        var service = Service("op-1", New("a1"));
        var vm = Vm(service);
        await vm.RefreshAsync();

        await vm.AcceptAsync("a1");
        await vm.StartAsync("a1", "rec-1");
        await vm.CompleteAsync("a1");

        Assert.Null(vm.LastError);
        Assert.Empty(vm.OpenRows);
        var done = Assert.Single(vm.CompletedRows);
        Assert.Equal("a1", done.AssignmentId);
        Assert.Equal("rec-1", done.Assignment.RecordId);
    }

    [Fact]
    public async Task Illegal_transition_surfaces_as_last_error_not_a_crash()
    {
        var service = Service("op-1", New("a1"));
        var vm = Vm(service);
        await vm.RefreshAsync();

        await vm.CompleteAsync("a1"); // not started yet

        Assert.NotNull(vm.LastError);
        Assert.Contains("Completed", vm.LastError);
        // The assignment is untouched and still open.
        Assert.Contains(vm.OpenRows, r => r.AssignmentId == "a1");
    }

    [Fact]
    public async Task Acting_on_another_operators_assignment_surfaces_an_access_error()
    {
        var service = Service("op-1", New("theirs", user: "op-2"));
        var vm = Vm(service);

        await vm.AcceptAsync("theirs");

        Assert.NotNull(vm.LastError);
        Assert.Contains("not the assignee", vm.LastError);
    }

    [Fact]
    public async Task Open_raises_request_without_transitioning()
    {
        var service = Service("op-1", New("a1"));
        var vm = Vm(service);
        await vm.RefreshAsync();

        FieldAssignment? requested = null;
        vm.OpenRequested += (_, a) => requested = a;
        vm.Open(vm.OpenRows.Single());

        Assert.NotNull(requested);
        Assert.Equal("a1", requested!.AssignmentId);
        Assert.Equal(AssignmentStatus.Assigned, requested.Status);
    }

    [Fact]
    public async Task Status_filter_narrows_the_loaded_inbox()
    {
        var done = New("done");
        done.Start("rec");
        done.Complete();
        var service = Service("op-1", New("open"), done);
        var vm = Vm(service);

        vm.StatusFilter = AssignmentStatus.Completed;
        await vm.RefreshAsync();

        Assert.Empty(vm.OpenRows);
        Assert.Equal(["done"], vm.CompletedRows.Select(r => r.AssignmentId));
    }

    private sealed class MemStore(params FieldAssignment[] seed) : IAssignmentStore
    {
        private readonly Dictionary<string, FieldAssignment> _byId =
            seed.ToDictionary(a => a.AssignmentId, StringComparer.Ordinal);

        public Task SaveAsync(FieldAssignment assignment, CancellationToken ct = default)
        {
            _byId[assignment.AssignmentId] = assignment;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FieldAssignment>> LoadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FieldAssignment>>(_byId.Values.ToList());

        public Task<IReadOnlyList<FieldAssignment>> LoadForUserAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FieldAssignment>>(
                _byId.Values.Where(a => a.AssignedToUserId == userId).ToList());

        public Task DeleteAsync(string assignmentId, CancellationToken ct = default)
        {
            _byId.Remove(assignmentId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
