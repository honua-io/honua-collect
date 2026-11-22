// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Assignments;

public enum AssignmentPriority
{
    Low,

    Normal,

    High,

    Urgent,
}

public enum AssignmentStatus
{
    Assigned,

    Accepted,

    InProgress,

    Completed,

    Declined,

    Reassigned,
}

public sealed class FieldAssignment
{
    public required string AssignmentId { get; init; }

    public required string FormId { get; init; }

    public required string AssignedToUserId { get; init; }

    public required string Title { get; init; }

    public string? Instructions { get; init; }

    public FieldGeoPoint? Location { get; init; }

    public DateTimeOffset? DueAtUtc { get; init; }

    public AssignmentPriority Priority { get; init; } = AssignmentPriority.Normal;

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public AssignmentStatus Status { get; private set; } = AssignmentStatus.Assigned;

    public string? RecordId { get; private set; }

    public DateTimeOffset? AcceptedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public bool IsOpen => Status is AssignmentStatus.Assigned or AssignmentStatus.Accepted or AssignmentStatus.InProgress;

    public bool IsOverdue(DateTimeOffset asOfUtc) => IsOpen && DueAtUtc is { } due && asOfUtc > due;

    public void Accept(DateTimeOffset? atUtc = null)
    {
        Require(AssignmentStatus.Assigned, AssignmentStatus.Accepted);
        Status = AssignmentStatus.Accepted;
        AcceptedAtUtc = atUtc ?? DateTimeOffset.UtcNow;
    }

    public void Decline()
    {
        Require(AssignmentStatus.Assigned, AssignmentStatus.Declined);
        Status = AssignmentStatus.Declined;
    }

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

    public void Complete(DateTimeOffset? atUtc = null)
    {
        Require(AssignmentStatus.InProgress, AssignmentStatus.Completed);
        Status = AssignmentStatus.Completed;
        CompletedAtUtc = atUtc ?? DateTimeOffset.UtcNow;
    }

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
