// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;

namespace Honua.Field.App.Services;

/// <summary>
/// Lazily initializes an <see cref="IRecordStore"/> through an async factory so both
/// <see cref="RecordBook"/> and <see cref="Core.History.RecordRevertService"/> share
/// one encrypted SQLite store without blocking startup.
/// </summary>
public sealed class LazyRecordStore : IRecordStore
{
    private readonly Func<Task<IRecordStore>> _factory;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private IRecordStore? _inner;

    public LazyRecordStore(Func<Task<IRecordStore>> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task SaveAsync(FieldRecordEntry entry, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).SaveAsync(entry, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<FieldRecordEntry>> LoadAllAsync(CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).LoadAllAsync(ct).ConfigureAwait(false);

    public async Task DeleteAsync(string recordId, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false)).DeleteAsync(recordId, ct).ConfigureAwait(false);

    internal async Task<IRecordStore> GetStoreAsync(CancellationToken ct = default)
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

            var created = await _factory().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Record store factory returned null.");
            Volatile.Write(ref _inner, created);
            return created;
        }
        finally
        {
            _initGate.Release();
        }
    }
}
