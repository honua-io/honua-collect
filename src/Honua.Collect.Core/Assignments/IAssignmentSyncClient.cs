namespace Honua.Collect.Core.Assignments;

/// <summary>
/// A server-issued assignment as it arrives on a pull. This is the wire shape the
/// dispatcher's "Assign Record" action produces (BACKLOG/#40); the client maps it
/// into a <see cref="FieldAssignment"/> in its persisted lifecycle state so a
/// status already advanced on the server (e.g. reassigned away) round-trips intact.
/// </summary>
public sealed record AssignmentSnapshot
{
    /// <summary>Stable assignment identifier.</summary>
    public required string AssignmentId { get; init; }

    /// <summary>Form the worker must capture against.</summary>
    public required string FormId { get; init; }

    /// <summary>Operator the assignment is dispatched to.</summary>
    public required string AssignedToUserId { get; init; }

    /// <summary>Human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>Optional instructions.</summary>
    public string? Instructions { get; init; }

    /// <summary>Optional work location latitude.</summary>
    public double? Latitude { get; init; }

    /// <summary>Optional work location longitude.</summary>
    public double? Longitude { get; init; }

    /// <summary>Optional due date.</summary>
    public DateTimeOffset? DueAtUtc { get; init; }

    /// <summary>Dispatcher-set urgency.</summary>
    public AssignmentPriority Priority { get; init; } = AssignmentPriority.Normal;

    /// <summary>UTC time the assignment was dispatched.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Server-side lifecycle status.</summary>
    public AssignmentStatus Status { get; init; } = AssignmentStatus.Assigned;

    /// <summary>Record linked on the server, if any.</summary>
    public string? RecordId { get; init; }

    /// <summary>Builds the snapshot from a local assignment (used to seed a fake server).</summary>
    /// <param name="assignment">The assignment to snapshot.</param>
    /// <returns>The wire snapshot.</returns>
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

    /// <summary>Maps the snapshot into a <see cref="FieldAssignment"/> in its server status.</summary>
    /// <returns>The materialized assignment.</returns>
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

/// <summary>
/// A local status change to push back to the dispatcher so they see live progress
/// (accepted/started/completed/declined) — the "live status back to dispatcher"
/// half of the unified loop (#40).
/// </summary>
public sealed record AssignmentStatusUpdate
{
    /// <summary>The assignment whose status changed.</summary>
    public required string AssignmentId { get; init; }

    /// <summary>The new lifecycle status.</summary>
    public required AssignmentStatus Status { get; init; }

    /// <summary>Record captured against the assignment, when it has started/completed.</summary>
    public string? RecordId { get; init; }

    /// <summary>UTC time the change happened on the device.</summary>
    public DateTimeOffset ChangedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Builds an update from an assignment's current state.</summary>
    /// <param name="assignment">The assignment to report.</param>
    /// <param name="changedAtUtc">Optional change timestamp.</param>
    /// <returns>The status update.</returns>
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

/// <summary>
/// The sync contract between the device and the assignment server (BACKLOG E5 /
/// #40): pull newly dispatched assignments for an operator, and push local status
/// changes back so the dispatcher sees live progress. This is a seam only — no live
/// server is wired here; production binds it to the GeoServices transport and tests
/// bind a fake.
/// </summary>
public interface IAssignmentSyncClient
{
    /// <summary>Pulls the assignments the server currently holds for an operator.</summary>
    /// <param name="userId">The operator to pull assignments for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The server's assignment snapshots for the operator.</returns>
    Task<IReadOnlyList<AssignmentSnapshot>> PullAsync(string userId, CancellationToken ct = default);

    /// <summary>Pushes a batch of local status changes back to the server.</summary>
    /// <param name="updates">The status updates to report.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PushAsync(IReadOnlyList<AssignmentStatusUpdate> updates, CancellationToken ct = default);
}
