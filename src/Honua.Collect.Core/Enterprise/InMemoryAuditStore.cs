// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly object _gate = new();
    private readonly List<AuditEntry> _entries = [];

    public Task<long> HeadSequenceAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Count == 0 ? -1L : _entries[^1].Sequence);
        }
    }

    public Task<string> HeadHashAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Count == 0 ? string.Empty : _entries[^1].Hash);
        }
    }

    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery? query = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<AuditEntry> result = _entries;
            if (query is not null)
            {
                result = result.Where(query.Matches);
            }

            return Task.FromResult<IReadOnlyList<AuditEntry>>(result.OrderBy(e => e.Sequence).ToList());
        }
    }
}
