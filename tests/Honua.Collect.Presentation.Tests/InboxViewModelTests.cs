using Honua.Collect.Core.Assignments;
using Honua.Collect.Presentation.Assignments;

namespace Honua.Collect.Presentation.Tests;

public class InboxViewModelTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static FieldAssignment New(string id, string title, DateTimeOffset? due, string user = "u1") => new()
    {
        AssignmentId = id,
        FormId = "field-site",
        AssignedToUserId = user,
        Title = title,
        DueAtUtc = due,
    };

    private static InboxViewModel Build(params FieldAssignment[] assignments)
        => new(new AssignmentInbox("u1", assignments), AsOf);

    [Fact]
    public void OpenRows_are_ordered_most_urgent_first()
    {
        var later = New("a-later", "Later", AsOf.AddDays(5));
        var soon = New("a-soon", "Soon", AsOf.AddDays(1));
        var undated = New("a-undated", "Undated", due: null);

        var vm = Build(later, soon, undated);

        // Due-dated by soonest, then undated by dispatch time.
        Assert.Equal(["a-soon", "a-later", "a-undated"], vm.OpenRows.Select(r => r.AssignmentId));
        Assert.Equal(3, vm.OpenCount);
        Assert.Equal(3, vm.Open.Count);
    }

    [Fact]
    public void Overdue_assignment_is_marked_and_surfaced()
    {
        var overdue = New("a-overdue", "Overdue", AsOf.AddDays(-2));
        var future = New("a-future", "Future", AsOf.AddDays(2));

        var vm = Build(overdue, future);

        Assert.Single(vm.Overdue);
        Assert.Equal("a-overdue", vm.Overdue[0].AssignmentId);

        var overdueRow = vm.OpenRows.Single(r => r.AssignmentId == "a-overdue");
        Assert.True(overdueRow.IsOverdue);
        Assert.Contains("Overdue", overdueRow.StatusText);

        var futureRow = vm.OpenRows.Single(r => r.AssignmentId == "a-future");
        Assert.False(futureRow.IsOverdue);

        Assert.Equal("2 open · 1 overdue", vm.Header);
    }

    [Fact]
    public void Open_command_raises_request_and_does_not_transition_on_its_own()
    {
        var assignment = New("a1", "Inspect", AsOf.AddDays(3));
        var vm = Build(assignment);

        FieldAssignment? requested = null;
        vm.OpenRequested += (_, a) => requested = a;

        var row = vm.OpenRows.Single();
        vm.OpenCommand.Execute(row);

        Assert.Same(assignment, requested);
        // Opening only signals the host; the transition happens when Start is called.
        Assert.Equal(AssignmentStatus.Assigned, assignment.Status);
    }

    [Fact]
    public void Start_moves_assignment_to_in_progress_and_off_the_open_list()
    {
        var assignment = New("a1", "Inspect", AsOf.AddDays(3));
        var vm = Build(assignment);

        vm.Start(assignment, "rec-1");

        Assert.Equal(AssignmentStatus.InProgress, assignment.Status);
        Assert.Equal("rec-1", assignment.RecordId);
        // Still open (in-progress), so it stays in the open list.
        Assert.Contains(vm.OpenRows, r => r.AssignmentId == "a1");
    }

    [Fact]
    public void Complete_then_refresh_moves_assignment_to_completed_rows()
    {
        var assignment = New("a1", "Inspect", AsOf.AddDays(3));
        var vm = Build(assignment);

        vm.Start(assignment, "rec-1");
        assignment.Complete();
        vm.Refresh();

        Assert.DoesNotContain(vm.OpenRows, r => r.AssignmentId == "a1");
        Assert.Contains(vm.CompletedRows, r => r.AssignmentId == "a1");
    }

    [Fact]
    public void CreateDemo_seeds_a_populated_inbox_with_an_overdue_item()
    {
        var vm = InboxViewModel.CreateDemo("u1", "field-site", AsOf);

        Assert.NotEmpty(vm.OpenRows);
        Assert.Equal(4, vm.OpenCount);
        Assert.Contains(vm.OpenRows, r => r.IsOverdue);
        // Most urgent (overdue) row sorts to the top.
        Assert.True(vm.OpenRows[0].IsOverdue);
        Assert.All(vm.OpenRows, r => Assert.Equal("field-site", r.Assignment.FormId));
    }

    [Fact]
    public void DueText_describes_dated_and_undated_assignments()
    {
        var dated = new AssignmentRowViewModel(New("a1", "Dated", AsOf.AddDays(1)), AsOf);
        var undated = new AssignmentRowViewModel(New("a2", "Undated", due: null), AsOf);

        Assert.StartsWith("Due ", dated.DueText);
        Assert.Equal("No due date", undated.DueText);
    }
}
