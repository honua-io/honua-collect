// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Records;

namespace Honua.Collect.Core.Storage;

public interface IRecordStore
{
    Task SaveAsync(CollectRecordEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<CollectRecordEntry>> LoadAllAsync(CancellationToken ct = default);

    Task DeleteAsync(string recordId, CancellationToken ct = default);
}
