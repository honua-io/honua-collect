using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Assignments;

/// <summary>Relative urgency a dispatcher sets on an assignment.</summary>
public enum AssignmentPriority
{
    /// <summary>Routine work; no special urgency.</summary>
    Low,

    /// <summary>Default urgency.</summary>
    Normal,

    /// <summary>Elevated urgency; surface above normal work.</summary>
    High,

    /// <summary>Drop-everything urgency.</summary>
    Urgent,
}

/// <summary>
/// Lifecycle status of a dispatched field assignment.
/// </summary>
public enum AssignmentStatus
{
    /// <summary>Dispatched to a worker but not yet acknowledged.</summary>
    Assigned,

    /// <summary>Worker has accepted the assignment.</summary>
    Accepted,

    /// <summary>Worker is actively capturing against it.</summary>
    InProgress,

    /// <summary>Capture is complete and the record has been submitted.</summary>
    Completed,

    /// <summary>Worker declined/rejected the assignment.</summary>
    Declined,

    /// <summary>Reassigned to a different worker; this copy is closed.</summary>
    Reassigned,
}

/// <summary>
/// A task dispatched to a field worker (BACKLOG E5): "go capture a record with
/// this form, here, by then." Assignments are the inbox half of the
/// dispatch → capture → submit loop that Survey123 ("Inbox") and Fulcrum
/// ("assignments") both provide; the captured <see cref="FieldRecord"/> is the
/// outbox half, linked back via <see cref="RecordId"/>.
/// </summary>
public sealed class FieldAssignment
{
    /// <summary>Stable assignment identifier.</summary>
    public required string AssignmentId { get; init; }

    /// <summary>Form the worker must capture against.</summary>
    public required string FormId { get; init; }

    /// <summary>User the assignment is dispatched to.</summary>
    public required string AssignedToUserId { get; init; }

    /// <summary>Human-readable title for the inbox list.</summary>
    public required string Title { get; init; }

    /// <summary>Optional instructions for the worker.</summary>
    public string? Instructions { get; init; }

    /// <summary>Optional location the work is to be performed at.</summary>
    public FieldGeoPoint? Location { get; init; }

    /// <summary>Optional due date.</summary>
    public DateTimeOffset? DueAtUtc { get; init; }

    /// <summary>Dispatcher-set urgency, surfaced in the inbox and used for ordering.</summary>
    public AssignmentPriority Priority { get; init; } = AssignmentPriority.Normal;

    /// <summary>UTC time the assignment was created/dispatched.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Current lifecycle status.</summary>
    public AssignmentStatus Status { get; private set; } = AssignmentStatus.Assigned;

    /// <summary>Identifier of the record created to fulfill this assignment, once started.</summary>
    public string? RecordId { get; private set; }

    /// <summary>UTC time the worker accepted, when applicable.</summary>
    public DateTimeOffset? AcceptedAtUtc { get; private set; }

    /// <summary>UTC time the assignment was completed, when applicable.</summary>
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>Whether the assignment still needs action (not completed or declined).</summary>
    public bool IsOpen => Status is AssignmentStatus.Assigned or AssignmentStatus.Accepted or AssignmentStatus.InProgress;

    /// <summary>Whether the assignment is past its due date and still open.</summary>
    /// <param name="asOfUtc">Reference time to compare against.</param>
    /// <returns><see langword="true"/> when overdue.</returns>
    public bool IsOverdue(DateTimeOffset asOfUtc) => IsOpen && DueAtUtc is { } due && asOfUtc > due;

    /// <summary>Accepts the assignment.</summary>
    /// <param name="atUtc">Optional timestamp.</param>
    public void Accept(DateTimeOffset? atUtc = null)
    {
        Require(AssignmentStatus.Assigned, AssignmentStatus.Accepted);
        Status = AssignmentStatus.Accepted;
        AcceptedAtUtc = atUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Declines the assignment.</summary>
    public void Decline()
    {
        Require(AssignmentStatus.Assigned, AssignmentStatus.Declined);
        Status = AssignmentStatus.Declined;
    }

    /// <summary>Starts capture, linking the record that fulfills the assignment.</summary>
    /// <param name="recordId">Identifier of the record being captured.</param>
    public void Start(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        // Accepting is implicit when starting straight from the inbox.
        if (Status == AssignmentStatus.Assigned)
        {
            Accept();
        }

        Require(AssignmentStatus.Accepted, AssignmentStatus.InProgress);
        Status = AssignmentStatus.InProgress;
        RecordId = recordId;
    }

    /// <summary>Marks the assignment complete.</summary>
    /// <param name="atUtc">Optional timestamp.</param>
    public void Complete(DateTimeOffset? atUtc = null)
    {
        Require(AssignmentStatus.InProgress, AssignmentStatus.Completed);
        Status = AssignmentStatus.Completed;
        CompletedAtUtc = atUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Reassigns the work to another operator. Only an open assignment that the
    /// current operator has not yet started can be reassigned; this copy is closed
    /// (<see cref="AssignmentStatus.Reassigned"/>) and a fresh assignment is returned
    /// for the new operator so the work survives but its lifecycle restarts cleanly.
    /// </summary>
    /// <param name="newUserId">The operator to reassign the work to.</param>
    /// <param name="newAssignmentId">Stable id for the new assignment copy.</param>
    /// <returns>A new, unstarted assignment dispatched to <paramref name="newUserId"/>.</returns>
    public FieldAssignment Reassign(string newUserId, string newAssignmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newAssignmentId);

        if (Status is not (AssignmentStatus.Assigned or AssignmentStatus.Accepted))
        {
            throw new InvalidOperationException(
                $"Assignment '{AssignmentId}' cannot be reassigned from {Status}; " +
                "only an unstarted (Assigned/Accepted) assignment can be reassigned.");
        }

        Status = AssignmentStatus.Reassigned;

        return new FieldAssignment
        {
            AssignmentId = newAssignmentId,
            FormId = FormId,
            AssignedToUserId = newUserId,
            Title = Title,
            Instructions = Instructions,
            Location = Location,
            DueAtUtc = DueAtUtc,
            Priority = Priority,
        };
    }

    private void Require(AssignmentStatus expected, AssignmentStatus target)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Assignment '{AssignmentId}' cannot move to {target} from {Status}; expected {expected}.");
        }
    }

    /// <summary>
    /// Rebuilds an assignment with its persisted lifecycle state, bypassing the
    /// transition guards. Used only by the store and sync layers to rehydrate a
    /// row/payload into its exact saved state; live transitions must still go
    /// through <see cref="Accept"/>/<see cref="Start"/>/<see cref="Complete"/>.
    /// </summary>
    internal void RestoreState(
        AssignmentStatus status,
        string? recordId,
        DateTimeOffset? acceptedAtUtc,
        DateTimeOffset? completedAtUtc)
    {
        Status = status;
        RecordId = recordId;
        AcceptedAtUtc = acceptedAtUtc;
        CompletedAtUtc = completedAtUtc;
    }
}
