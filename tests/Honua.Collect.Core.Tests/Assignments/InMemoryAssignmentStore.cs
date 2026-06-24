using Honua.Collect.Core.Assignments;

namespace Honua.Collect.Core.Tests.Assignments;

/// <summary>In-memory <see cref="IAssignmentStore"/> for fast service-logic tests.</summary>
internal sealed class InMemoryAssignmentStore : IAssignmentStore
{
    private readonly Dictionary<string, FieldAssignment> _byId = new(StringComparer.Ordinal);

    public InMemoryAssignmentStore(params FieldAssignment[] seed)
    {
        foreach (var assignment in seed)
        {
            _byId[assignment.AssignmentId] = assignment;
        }
    }

    public Task SaveAsync(FieldAssignment assignment, CancellationToken ct = default)
    {
        _byId[assignment.AssignmentId] = assignment;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FieldAssignment>> LoadAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FieldAssignment>>(_byId.Values.ToList());

    public Task<IReadOnlyList<FieldAssignment>> LoadForUserAsync(string userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FieldAssignment>>(
            _byId.Values.Where(a => string.Equals(a.AssignedToUserId, userId, StringComparison.Ordinal)).ToList());

    public Task DeleteAsync(string assignmentId, CancellationToken ct = default)
    {
        _byId.Remove(assignmentId);
        return Task.CompletedTask;
    }
}
