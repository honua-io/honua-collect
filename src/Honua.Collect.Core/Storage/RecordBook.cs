using Honua.Collect.Core.Records;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Storage;

/// <summary>
/// The in-memory book of captured records, written through to a durable
/// <see cref="IRecordStore"/>. Replaces the former static <c>CaptureStore</c>: it
/// is an injectable, thread-safe singleton so the capture, records, sync, export,
/// and inbox screens share one consistent, persisted view without racing on
/// shared mutable state.
/// </summary>
/// <remarks>
/// The backing store is created lazily through an async factory, so the app can
/// fetch the at-rest encryption key from secure storage off the UI thread rather
/// than blocking startup.
/// </remarks>
public sealed class RecordBook
{
    private readonly Func<Task<IRecordStore>> _storeFactory;
    private readonly List<CollectRecordEntry> _entries = [];
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly object _listGate = new();
    private IRecordStore? _store;
    private volatile bool _initialized;

    /// <summary>Creates the book over an async store factory (used to inject an encryption key).</summary>
    /// <param name="storeFactory">Builds the durable store on first use.</param>
    public RecordBook(Func<Task<IRecordStore>> storeFactory)
        => _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));

    /// <summary>Creates the book over an already-constructed store (convenience for tests/simple hosts).</summary>
    /// <param name="store">The backing record store.</param>
    public RecordBook(IRecordStore store)
        : this(() => Task.FromResult(store ?? throw new ArgumentNullException(nameof(store))))
    {
    }

    /// <summary>
    /// Builds the store (once) and hydrates the book from it, idempotently and
    /// safely under concurrency. Subsequent calls are no-ops.
    /// </summary>
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

    /// <summary>A point-in-time snapshot of all tracked entries, newest first.</summary>
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

    /// <summary>Adds a freshly submitted record to the Outbox and persists it.</summary>
    /// <param name="record">The submitted record (review status past Draft).</param>
    /// <returns>The tracked entry.</returns>
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

    /// <summary>Persists the current state of an entry (idempotent upsert).</summary>
    /// <param name="entry">The entry to persist.</param>
    public async Task SaveAsync(CollectRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await InitializeAsync().ConfigureAwait(false);
        await _store!.SaveAsync(entry).ConfigureAwait(false);
    }
}
