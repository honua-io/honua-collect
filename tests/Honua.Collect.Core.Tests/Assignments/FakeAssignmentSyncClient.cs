// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Assignments;

namespace Honua.Collect.Core.Tests.Assignments;

internal sealed class FakeAssignmentSyncClient : IAssignmentSyncClient
{
    private readonly Dictionary<string, List<AssignmentSnapshot>> _serverByUser = new(StringComparer.Ordinal);

    public List<AssignmentStatusUpdate> PushedUpdates { get; } = [];

    public int PushCallCount { get; private set; }

    public void Seed(params AssignmentSnapshot[] snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (!_serverByUser.TryGetValue(snapshot.AssignedToUserId, out var list))
            {
                list = [];
                _serverByUser[snapshot.AssignedToUserId] = list;
            }

            list.Add(snapshot);
        }
    }

    public Task<IReadOnlyList<AssignmentSnapshot>> PullAsync(string userId, CancellationToken ct = default)
    {
        IReadOnlyList<AssignmentSnapshot> result = _serverByUser.TryGetValue(userId, out var list)
            ? list.ToList()
            : [];
        return Task.FromResult(result);
    }

    public Task PushAsync(IReadOnlyList<AssignmentStatusUpdate> updates, CancellationToken ct = default)
    {
        PushCallCount++;
        PushedUpdates.AddRange(updates);
        return Task.CompletedTask;
    }
}
