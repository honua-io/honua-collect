// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public sealed class LazyAuditStore : IAuditStore
{
    private readonly Func<CancellationToken, Task<IAuditStore>> _storeFactory;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private IAuditStore? _inner;

    public LazyAuditStore(Func<CancellationToken, Task<IAuditStore>> storeFactory)
    {
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    }

    public async Task<long> HeadSequenceAsync(CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).HeadSequenceAsync(ct).ConfigureAwait(false);

    public async Task<string> HeadHashAsync(CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).HeadHashAsync(ct).ConfigureAwait(false);

    public async Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).AppendAsync(entry, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery? query = null, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).QueryAsync(query, ct).ConfigureAwait(false);

    private async Task<IAuditStore> GetStoreAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref _inner);
        if (existing is not null)
        {
            return existing;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            existing = Volatile.Read(ref _inner);
            if (existing is not null)
            {
                return existing;
            }

            var created = await _storeFactory(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Audit store factory returned null.");
            Volatile.Write(ref _inner, created);
            return created;
        }
        finally
        {
            _initGate.Release();
        }
    }
}
