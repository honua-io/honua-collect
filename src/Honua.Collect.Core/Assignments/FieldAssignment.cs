using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Assignments;

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

    /// <summary>Worker declined the assignment.</summary>
    Declined,
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

    private void Require(AssignmentStatus expected, AssignmentStatus target)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Assignment '{AssignmentId}' cannot move to {target} from {Status}; expected {expected}.");
        }
    }
}
