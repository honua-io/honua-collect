namespace Honua.Collect.Core.Assignments;

/// <summary>
/// A field worker's inbox of dispatched <see cref="FieldAssignment"/>s
/// (BACKLOG E5). Filters and orders assignments for the list screen and
/// surfaces the open/overdue counts a worker needs at a glance.
/// </summary>
public sealed class AssignmentInbox
{
    private readonly List<FieldAssignment> _assignments;

    /// <summary>Creates an inbox for a user from a set of assignments.</summary>
    /// <param name="userId">The worker the inbox belongs to.</param>
    /// <param name="assignments">Assignments to include (filtered to this user).</param>
    public AssignmentInbox(string userId, IEnumerable<FieldAssignment> assignments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(assignments);

        UserId = userId;
        _assignments = assignments
            .Where(a => string.Equals(a.AssignedToUserId, userId, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>The worker this inbox belongs to.</summary>
    public string UserId { get; }

    /// <summary>All assignments for the worker.</summary>
    public IReadOnlyList<FieldAssignment> All => _assignments;

    /// <summary>
    /// Open assignments (assigned/accepted/in-progress), ordered with the most
    /// urgent first: due-dated items by soonest due date, then undated items by
    /// dispatch time.
    /// </summary>
    public IReadOnlyList<FieldAssignment> Open => _assignments
        .Where(a => a.IsOpen)
        .OrderBy(a => a.DueAtUtc ?? DateTimeOffset.MaxValue)
        .ThenBy(a => a.CreatedAtUtc)
        .ToList();

    /// <summary>Completed assignments.</summary>
    public IReadOnlyList<FieldAssignment> Completed => _assignments
        .Where(a => a.Status == AssignmentStatus.Completed)
        .ToList();

    /// <summary>Open assignments whose due date has passed.</summary>
    /// <param name="asOfUtc">Reference time.</param>
    /// <returns>Overdue assignments.</returns>
    public IReadOnlyList<FieldAssignment> Overdue(DateTimeOffset asOfUtc) => _assignments
        .Where(a => a.IsOverdue(asOfUtc))
        .OrderBy(a => a.DueAtUtc)
        .ToList();

    /// <summary>Count of assignments still needing action.</summary>
    public int OpenCount => _assignments.Count(a => a.IsOpen);
}
