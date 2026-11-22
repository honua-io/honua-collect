// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Assignments;

public interface IAssignmentStore
{
    Task SaveAsync(FieldAssignment assignment, CancellationToken ct = default);

    Task<IReadOnlyList<FieldAssignment>> LoadAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<FieldAssignment>> LoadForUserAsync(string userId, CancellationToken ct = default);

    Task DeleteAsync(string assignmentId, CancellationToken ct = default);
}
