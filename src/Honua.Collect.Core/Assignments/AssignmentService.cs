// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Assignments;

public sealed class AssignmentAccessException : InvalidOperationException
{
    public AssignmentAccessException(string userId, string assignmentId)
        : base($"Operator '{userId}' is not the assignee of assignment '{assignmentId}'.")
    {
        UserId = userId;
        AssignmentId = assignmentId;
    }

    public string UserId { get; }

    public string AssignmentId { get; }
}

public sealed class AssignmentService
{
    private readonly IAssignmentStore _store;
    private readonly IAuthSessionStore _sessions;
    private readonly IAssignmentSyncClient? _sync;
    private readonly TimeProvider _clock;

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

    private string CurrentUserId =>
        _sessions.Current?.UserId
        ?? throw new InvalidOperationException("No operator is signed in; cannot act on assignments.");

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

    public async Task<IReadOnlyDictionary<AssignmentStatus, int>> GetStatusCountsAsync(
        CancellationToken ct = default)
    {
        var assignments = await _store.LoadForUserAsync(CurrentUserId, ct).ConfigureAwait(false);
        return assignments
            .GroupBy(a => a.Status)
            .ToDictionary(g => g.Key, g => g.Count());
    }

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

    public Task<FieldAssignment> AcceptAsync(string assignmentId, CancellationToken ct = default)
        => MutateAsync(assignmentId, a => a.Accept(Now), ct);

    public Task<FieldAssignment> DeclineAsync(string assignmentId, CancellationToken ct = default)
        => MutateAsync(assignmentId, a => a.Decline(), ct);

    public Task<FieldAssignment> StartAsync(string assignmentId, string recordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return MutateAsync(assignmentId, a => a.Start(recordId), ct);
    }

    public Task<FieldAssignment> CompleteAsync(string assignmentId, CancellationToken ct = default)
        => MutateAsync(assignmentId, a => a.Complete(Now), ct);

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
