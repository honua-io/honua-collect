// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.History;

namespace Honua.Field.App.Services;

/// <summary>
/// Lazily initializes an <see cref="IRecordHistoryStore"/> through an async factory
/// so the encryption key is fetched from secure storage off the startup thread.
/// </summary>
internal sealed class LazyRecordHistoryStore : IRecordHistoryStore
{
    private readonly Func<CancellationToken, Task<IRecordHistoryStore>> _factory;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private IRecordHistoryStore? _inner;

    public LazyRecordHistoryStore(Func<CancellationToken, Task<IRecordHistoryStore>> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task AppendAsync(string recordId, RecordEdit edit, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).AppendAsync(recordId, edit, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RecordEdit>> GetHistoryAsync(string recordId, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).GetHistoryAsync(recordId, ct).ConfigureAwait(false);

    public async Task<int> GetNextSequenceAsync(string recordId, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).GetNextSequenceAsync(recordId, ct).ConfigureAwait(false);

    private async Task<IRecordHistoryStore> GetStoreAsync(CancellationToken ct)
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

            var created = await _factory(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Record history store factory returned null.");
            Volatile.Write(ref _inner, created);
            return created;
        }
        finally
        {
            _initGate.Release();
        }
    }
}
