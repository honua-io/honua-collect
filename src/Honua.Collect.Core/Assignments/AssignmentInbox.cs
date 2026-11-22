// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Assignments;

public sealed class AssignmentInbox
{
    private readonly List<FieldAssignment> _assignments;

    public AssignmentInbox(string userId, IEnumerable<FieldAssignment> assignments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(assignments);

        UserId = userId;
        _assignments = assignments
            .Where(a => string.Equals(a.AssignedToUserId, userId, StringComparison.Ordinal))
            .ToList();
    }

    public string UserId { get; }

    public IReadOnlyList<FieldAssignment> All => _assignments;

    public IReadOnlyList<FieldAssignment> Open => _assignments
        .Where(a => a.IsOpen)
        .OrderBy(a => a.DueAtUtc ?? DateTimeOffset.MaxValue)
        .ThenBy(a => a.CreatedAtUtc)
        .ToList();

    public IReadOnlyList<FieldAssignment> Completed => _assignments
        .Where(a => a.Status == AssignmentStatus.Completed)
        .ToList();

    public IReadOnlyList<FieldAssignment> Overdue(DateTimeOffset asOfUtc) => _assignments
        .Where(a => a.IsOverdue(asOfUtc))
        .OrderBy(a => a.DueAtUtc)
        .ToList();

    public int OpenCount => _assignments.Count(a => a.IsOpen);
}
