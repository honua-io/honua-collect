// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Related;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App.Services;

/// <summary>
/// Lazily initializes an <see cref="IRelatedRecordStore"/> through an async factory
/// so the encryption key is fetched from secure storage off the startup thread.
/// </summary>
internal sealed class LazyRelatedRecordStore : IRelatedRecordStore
{
    private readonly Func<CancellationToken, Task<IRelatedRecordStore>> _factory;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private IRelatedRecordStore? _inner;

    public LazyRelatedRecordStore(Func<CancellationToken, Task<IRelatedRecordStore>> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task LinkAsync(
        string parentRecordId,
        string fieldId,
        FieldRecordLinkValue link,
        RecordLinkBehavior behavior = RecordLinkBehavior.Cascade,
        CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false))
            .LinkAsync(parentRecordId, fieldId, link, behavior, ct).ConfigureAwait(false);

    public async Task<bool> UnlinkAsync(string parentRecordId, string fieldId, string childRecordId, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false))
            .UnlinkAsync(parentRecordId, fieldId, childRecordId, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<FieldRecordLinkValue>> ListAsync(string parentRecordId, string fieldId, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false))
            .ListAsync(parentRecordId, fieldId, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> DeleteParentAsync(string parentRecordId, CancellationToken ct = default)
        => await (await GetStoreAsync(ct).ConfigureAwait(false))
            .DeleteParentAsync(parentRecordId, ct).ConfigureAwait(false);

    private async Task<IRelatedRecordStore> GetStoreAsync(CancellationToken ct)
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
                ?? throw new InvalidOperationException("Related-record store factory returned null.");
            Volatile.Write(ref _inner, created);
            return created;
        }
        finally
        {
            _initGate.Release();
        }
    }
}
