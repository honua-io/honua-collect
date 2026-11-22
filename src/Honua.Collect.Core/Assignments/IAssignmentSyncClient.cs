// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Assignments;

public sealed record AssignmentSnapshot
{
    public required string AssignmentId { get; init; }

    public required string FormId { get; init; }

    public required string AssignedToUserId { get; init; }

    public required string Title { get; init; }

    public string? Instructions { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public DateTimeOffset? DueAtUtc { get; init; }

    public AssignmentPriority Priority { get; init; } = AssignmentPriority.Normal;

    public DateTimeOffset CreatedAtUtc { get; init; }

    public AssignmentStatus Status { get; init; } = AssignmentStatus.Assigned;

    public string? RecordId { get; init; }

    public static AssignmentSnapshot From(FieldAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return new AssignmentSnapshot
        {
            AssignmentId = assignment.AssignmentId,
            FormId = assignment.FormId,
            AssignedToUserId = assignment.AssignedToUserId,
            Title = assignment.Title,
            Instructions = assignment.Instructions,
            Latitude = assignment.Location?.Latitude,
            Longitude = assignment.Location?.Longitude,
            DueAtUtc = assignment.DueAtUtc,
            Priority = assignment.Priority,
            CreatedAtUtc = assignment.CreatedAtUtc,
            Status = assignment.Status,
            RecordId = assignment.RecordId,
        };
    }

    public FieldAssignment ToAssignment()
    {
        var assignment = new FieldAssignment
        {
            AssignmentId = AssignmentId,
            FormId = FormId,
            AssignedToUserId = AssignedToUserId,
            Title = Title,
            Instructions = Instructions,
            Location = Latitude is { } lat && Longitude is { } lon
                ? new Sdk.Field.Records.FieldGeoPoint(lat, lon, null)
                : null,
            DueAtUtc = DueAtUtc,
            Priority = Priority,
            CreatedAtUtc = CreatedAtUtc,
        };

        // A freshly dispatched assignment is already Assigned; only restore when the
        // server has advanced it, so the common case keeps the constructor default.
        if (Status != AssignmentStatus.Assigned || RecordId is not null)
        {
            assignment.RestoreState(Status, RecordId, acceptedAtUtc: null, completedAtUtc: null);
        }

        return assignment;
    }
}

public sealed record AssignmentStatusUpdate
{
    public required string AssignmentId { get; init; }

    public required AssignmentStatus Status { get; init; }

    public string? RecordId { get; init; }

    public DateTimeOffset ChangedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static AssignmentStatusUpdate From(FieldAssignment assignment, DateTimeOffset? changedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return new AssignmentStatusUpdate
        {
            AssignmentId = assignment.AssignmentId,
            Status = assignment.Status,
            RecordId = assignment.RecordId,
            ChangedAtUtc = changedAtUtc ?? DateTimeOffset.UtcNow,
        };
    }
}

public interface IAssignmentSyncClient
{
    Task<IReadOnlyList<AssignmentSnapshot>> PullAsync(string userId, CancellationToken ct = default);

    Task PushAsync(IReadOnlyList<AssignmentStatusUpdate> updates, CancellationToken ct = default);
}
