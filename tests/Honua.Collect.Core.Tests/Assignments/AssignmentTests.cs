using Honua.Collect.Core.Assignments;

namespace Honua.Collect.Core.Tests.Assignments;

public class AssignmentTests
{
    private static FieldAssignment New(
        string id = "a1",
        string user = "u1",
        DateTimeOffset? due = null) => new()
    {
        AssignmentId = id,
        FormId = "f",
        AssignedToUserId = user,
        Title = "Inspect hydrant",
        DueAtUtc = due,
        CreatedAtUtc = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Capture_loop_runs_assigned_to_completed()
    {
        var a = New();
        Assert.Equal(AssignmentStatus.Assigned, a.Status);

        a.Start("rec-1"); // accepts implicitly
        Assert.Equal(AssignmentStatus.InProgress, a.Status);
        Assert.Equal("rec-1", a.RecordId);
        Assert.NotNull(a.AcceptedAtUtc);

        a.Complete();
        Assert.Equal(AssignmentStatus.Completed, a.Status);
        Assert.False(a.IsOpen);
        Assert.NotNull(a.CompletedAtUtc);
    }

    [Fact]
    public void Decline_closes_the_assignment()
    {
        var a = New();
        a.Decline();

        Assert.Equal(AssignmentStatus.Declined, a.Status);
        Assert.False(a.IsOpen);
    }

    [Fact]
    public void Invalid_transitions_are_rejected()
    {
        var a = New();
        Assert.Throws<InvalidOperationException>(() => a.Complete()); // not in progress

        a.Decline();
        Assert.Throws<InvalidOperationException>(() => a.Accept()); // already closed
    }

    [Fact]
    public void Overdue_only_applies_to_open_assignments()
    {
        var now = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var due = new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero);

        var open = New(due: due);
        Assert.True(open.IsOverdue(now));

        open.Start("r");
        open.Complete();
        Assert.False(open.IsOverdue(now)); // completed is never overdue
    }

    [Fact]
    public void Inbox_filters_to_the_user_and_orders_open_by_urgency()
    {
        var soon = new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero);

        var mineLater = New("a-later", "u1", later);
        var mineSoon = New("a-soon", "u1", soon);
        var mineUndated = New("a-undated", "u1");
        var someoneElse = New("a-other", "u2", soon);

        var inbox = new AssignmentInbox("u1", [mineLater, mineSoon, mineUndated, someoneElse]);

        Assert.Equal(3, inbox.All.Count); // u2's assignment excluded
        Assert.Equal(["a-soon", "a-later", "a-undated"], inbox.Open.Select(a => a.AssignmentId));
        Assert.Equal(3, inbox.OpenCount);
    }

    [Fact]
    public void Inbox_separates_completed_and_overdue()
    {
        var now = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var overdue = New("a-od", "u1", new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero));
        var done = New("a-done", "u1");
        done.Start("r");
        done.Complete();

        var inbox = new AssignmentInbox("u1", [overdue, done]);

        Assert.Equal(["a-od"], inbox.Overdue(now).Select(a => a.AssignmentId));
        Assert.Equal(["a-done"], inbox.Completed.Select(a => a.AssignmentId));
        Assert.Equal(1, inbox.OpenCount);
    }
}
