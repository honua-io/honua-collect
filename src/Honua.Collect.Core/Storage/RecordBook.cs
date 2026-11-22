// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Records;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Storage;

public sealed class RecordBook
{
    private readonly Func<Task<IRecordStore>> _storeFactory;
    private readonly List<CollectRecordEntry> _entries = [];
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly object _listGate = new();
    private IRecordStore? _store;
    private volatile bool _initialized;

    public RecordBook(Func<Task<IRecordStore>> storeFactory)
        => _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));

    public RecordBook(IRecordStore store)
        : this(() => Task.FromResult(store ?? throw new ArgumentNullException(nameof(store))))
    {
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            _store ??= await _storeFactory().ConfigureAwait(false);
            var loaded = await _store.LoadAllAsync().ConfigureAwait(false);
            lock (_listGate)
            {
                _entries.Clear();
                _entries.AddRange(loaded.OrderByDescending(e => e.Record.CreatedAtUtc));
            }

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public IReadOnlyList<CollectRecordEntry> All
    {
        get
        {
            lock (_listGate)
            {
                return _entries.ToList();
            }
        }
    }

    public async Task<CollectRecordEntry> AddSubmittedAsync(FieldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        await InitializeAsync().ConfigureAwait(false);

        var entry = new CollectRecordEntry(record);
        if (record.Status != RecordStatus.Draft)
        {
            entry.MarkPending();
        }

        lock (_listGate)
        {
            _entries.Insert(0, entry);
        }

        await SaveAsync(entry).ConfigureAwait(false);
        return entry;
    }

    public async Task SaveAsync(CollectRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await InitializeAsync().ConfigureAwait(false);
        await _store!.SaveAsync(entry).ConfigureAwait(false);
    }
}
