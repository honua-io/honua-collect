// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.History;

public interface IRecordHistoryStore
{
    Task AppendAsync(string recordId, RecordEdit edit, CancellationToken ct = default);

    Task<IReadOnlyList<RecordEdit>> GetHistoryAsync(string recordId, CancellationToken ct = default);

    Task<int> GetNextSequenceAsync(string recordId, CancellationToken ct = default);
}
