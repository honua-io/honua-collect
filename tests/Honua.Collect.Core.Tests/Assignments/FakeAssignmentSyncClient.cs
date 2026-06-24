using Honua.Collect.Core.Assignments;

namespace Honua.Collect.Core.Tests.Assignments;

/// <summary>
/// In-memory <see cref="IAssignmentSyncClient"/> standing in for the server: pull
/// returns whatever snapshots were seeded for a user; push records every batch it
/// received so a test can assert the dispatcher saw the operator's progress.
/// </summary>
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
