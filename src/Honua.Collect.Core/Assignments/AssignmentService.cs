using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Assignments;

/// <summary>
/// Raised when an operator tries to act on an assignment that is not theirs. The
/// inbox is per-operator: only the assignee may accept, start, complete, or decline
/// their own work.
/// </summary>
public sealed class AssignmentAccessException : InvalidOperationException
{
    /// <summary>Creates the exception for an operator/assignment mismatch.</summary>
    /// <param name="userId">The operator that attempted the action.</param>
    /// <param name="assignmentId">The assignment they are not the assignee of.</param>
    public AssignmentAccessException(string userId, string assignmentId)
        : base($"Operator '{userId}' is not the assignee of assignment '{assignmentId}'.")
    {
        UserId = userId;
        AssignmentId = assignmentId;
    }

    /// <summary>The operator that attempted the action.</summary>
    public string UserId { get; }

    /// <summary>The assignment they are not assigned to.</summary>
    public string AssignmentId { get; }
}

/// <summary>
/// The Core dispatch + inbox service for field assignments (BACKLOG E5, finished by
/// #40). It owns the operator-scoped lifecycle on top of <see cref="IAssignmentStore"/>:
/// it reads the current operator from the live <see cref="AuthSession"/>
/// (<see cref="IAuthSessionStore"/>), enforces that only the assignee acts on their
/// own work, gates dispatch/reassign behind the <see cref="CollectPermission.ManageAssignments"/>
/// role (<see cref="DevicePrincipal"/>), persists every transition, and queues a
/// status update to push back to the dispatcher. Pull/push go through the injected
/// <see cref="IAssignmentSyncClient"/> seam (a fake in tests; no live server here).
/// </summary>
public sealed class AssignmentService
{
    private readonly IAssignmentStore _store;
    private readonly IAuthSessionStore _sessions;
    private readonly IAssignmentSyncClient? _sync;
    private readonly TimeProvider _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="store">Durable assignment store.</param>
    /// <param name="sessions">Live session store the operator identity is read from.</param>
    /// <param name="sync">Optional sync client; when null, pull/push are unavailable.</param>
    /// <param name="clock">Time source (defaults to system); injectable for tests.</param>
    public AssignmentService(
        IAssignmentStore store,
        IAuthSessionStore sessions,
        IAssignmentSyncClient? sync = null,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sync = sync;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>The signed-in operator's id, or throws when signed out.</summary>
    private string CurrentUserId =>
        _sessions.Current?.UserId
        ?? throw new InvalidOperationException("No operator is signed in; cannot act on assignments.");

    /// <summary>
    /// Builds the current operator's inbox from the store, scoped to their identity
    /// (their assignments only). Optionally narrows to a single status.
    /// </summary>
    /// <param name="status">When set, includes only assignments in this status.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operator's inbox.</returns>
    public async Task<AssignmentInbox> GetInboxAsync(
        AssignmentStatus? status = null,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var assignments = await _store.LoadForUserAsync(userId, ct).ConfigureAwait(false);
        if (status is { } wanted)
        {
            assignments = assignments.Where(a => a.Status == wanted).ToList();
        }

        return new AssignmentInbox(userId, assignments);
    }

    /// <summary>Counts the current operator's assignments by status.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A status → count map (only non-zero statuses appear).</returns>
    public async Task<IReadOnlyDictionary<AssignmentStatus, int>> GetStatusCountsAsync(
        CancellationToken ct = default)
    {
        var assignments = await _store.LoadForUserAsync(CurrentUserId, ct).ConfigureAwait(false);
        return assignments
            .GroupBy(a => a.Status)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Dispatches a new assignment to an operator. The caller must hold
    /// <see cref="CollectPermission.ManageAssignments"/>; the assignment is persisted
    /// and a fresh, unstarted <see cref="FieldAssignment"/> returned.
    /// </summary>
    /// <param name="dispatcher">The dispatching principal (role-checked).</param>
    /// <param name="assignment">The assignment to dispatch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted assignment.</returns>
    public async Task<FieldAssignment> DispatchAsync(
        DevicePrincipal dispatcher,
        FieldAssignment assignment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(assignment);
        dispatcher.Require(CollectPermission.ManageAssignments);

        await _store.SaveAsync(assignment, ct).ConfigureAwait(false);
        return assignment;
    }

    /// <summary>Accepts an assignment on behalf of the current operator (assignee-only).</summary>
    /// <param name="assignmentId">The assignment to accept.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated assignment.</returns>
    public Task<FieldAssignment> AcceptAsync(string assignmentId, CancellationToken ct = default)
        => MutateAsync(assignmentId, a => a.Accept(Now), ct);

    /// <summary>Declines an assignment on behalf of the current operator (assignee-only).</summary>
    /// <param name="assignmentId">The assignment to decline.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated assignment.</returns>
    public Task<FieldAssignment> DeclineAsync(string assignmentId, CancellationToken ct = default)
        => MutateAsync(assignmentId, a => a.Decline(), ct);

    /// <summary>
    /// Starts capture for an assignment, linking the record being captured
    /// (assignee-only). Guards reject completing before a start.
    /// </summary>
    /// <param name="assignmentId">The assignment to start.</param>
    /// <param name="recordId">Identifier of the record being captured.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated assignment.</returns>
    public Task<FieldAssignment> StartAsync(string assignmentId, string recordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return MutateAsync(assignmentId, a => a.Start(recordId), ct);
    }

    /// <summary>
    /// Completes an assignment (assignee-only). The assignment must be in progress —
    /// i.e. a record was captured — so completion always links a record.
    /// </summary>
    /// <param name="assignmentId">The assignment to complete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated assignment.</returns>
    public Task<FieldAssignment> CompleteAsync(string assignmentId, CancellationToken ct = default)
        => MutateAsync(assignmentId, a => a.Complete(Now), ct);

    /// <summary>
    /// Reassigns an unstarted assignment to a different operator. The caller must hold
    /// <see cref="CollectPermission.ManageAssignments"/>. The original copy is closed
    /// (<see cref="AssignmentStatus.Reassigned"/>) and a fresh assignment is persisted
    /// for the new operator.
    /// </summary>
    /// <param name="dispatcher">The dispatching principal (role-checked).</param>
    /// <param name="assignmentId">The assignment to reassign.</param>
    /// <param name="newUserId">The operator to reassign to.</param>
    /// <param name="newAssignmentId">Stable id for the new assignment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new assignment for the new operator.</returns>
    public async Task<FieldAssignment> ReassignAsync(
        DevicePrincipal dispatcher,
        string assignmentId,
        string newUserId,
        string newAssignmentId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.Require(CollectPermission.ManageAssignments);

        var original = await RequireAsync(assignmentId, ct).ConfigureAwait(false);
        var moved = original.Reassign(newUserId, newAssignmentId);

        await _store.SaveAsync(original, ct).ConfigureAwait(false);
        await _store.SaveAsync(moved, ct).ConfigureAwait(false);
        return moved;
    }

    /// <summary>
    /// Pulls the current operator's assignments from the server and persists them,
    /// then returns the refreshed inbox. New snapshots are upserted; locally advanced
    /// statuses for the same id are left intact (the server only seeds new work here).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The refreshed inbox.</returns>
    public async Task<AssignmentInbox> PullAsync(CancellationToken ct = default)
    {
        var sync = RequireSync();
        var userId = CurrentUserId;

        var snapshots = await sync.PullAsync(userId, ct).ConfigureAwait(false);
        var existing = (await _store.LoadForUserAsync(userId, ct).ConfigureAwait(false))
            .ToDictionary(a => a.AssignmentId, StringComparer.Ordinal);

        foreach (var snapshot in snapshots)
        {
            // Don't clobber a status the operator has already advanced locally;
            // only persist genuinely new assignments from the server.
            if (existing.ContainsKey(snapshot.AssignmentId))
            {
                continue;
            }

            await _store.SaveAsync(snapshot.ToAssignment(), ct).ConfigureAwait(false);
        }

        return await GetInboxAsync(ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes the current operator's local status changes back to the dispatcher. By
    /// default it reports every assignment the operator has acted on (accepted or
    /// later); pass a predicate to narrow the set.
    /// </summary>
    /// <param name="filter">Optional predicate selecting which assignments to report.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of status updates pushed.</returns>
    public async Task<int> PushStatusAsync(
        Func<FieldAssignment, bool>? filter = null,
        CancellationToken ct = default)
    {
        var sync = RequireSync();
        var assignments = await _store.LoadForUserAsync(CurrentUserId, ct).ConfigureAwait(false);

        filter ??= a => a.Status != AssignmentStatus.Assigned;
        var updates = assignments
            .Where(filter)
            .Select(a => AssignmentStatusUpdate.From(a, Now))
            .ToList();

        if (updates.Count > 0)
        {
            await sync.PushAsync(updates, ct).ConfigureAwait(false);
        }

        return updates.Count;
    }

    private async Task<FieldAssignment> MutateAsync(
        string assignmentId,
        Action<FieldAssignment> transition,
        CancellationToken ct)
    {
        var assignment = await RequireAsync(assignmentId, ct).ConfigureAwait(false);

        // Only-assignee-acts: the inbox lifecycle belongs to the operator it was
        // dispatched to, regardless of what the store hands back.
        if (!string.Equals(assignment.AssignedToUserId, CurrentUserId, StringComparison.Ordinal))
        {
            throw new AssignmentAccessException(CurrentUserId, assignmentId);
        }

        transition(assignment);
        await _store.SaveAsync(assignment, ct).ConfigureAwait(false);
        return assignment;
    }

    private async Task<FieldAssignment> RequireAsync(string assignmentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assignmentId);
        var all = await _store.LoadAllAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(a => string.Equals(a.AssignmentId, assignmentId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Assignment '{assignmentId}' was not found.");
    }

    private IAssignmentSyncClient RequireSync()
        => _sync ?? throw new InvalidOperationException(
            "No assignment sync client is configured; pull/push are unavailable.");
}
