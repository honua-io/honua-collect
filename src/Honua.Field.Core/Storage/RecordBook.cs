// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Storage;

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
    private readonly List<FieldRecordEntry> _entries = [];
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
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
    public IReadOnlyList<FieldRecordEntry> All
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
    /// <param name="submission">Optional versioned submission captured with this record.</param>
    /// <param name="editorSnapshot">Optional editor state retained for correcting a confirmed rejection.</param>
    /// <param name="ordinaryPhotos">Captured photos for an ordinary feature.</param>
    /// <returns>The tracked entry.</returns>
    public async Task<FieldRecordEntry> AddSubmittedAsync(FieldRecord record, PublishedFormSubmission? submission = null,
        PublishedFormDraft? editorSnapshot = null, IReadOnlyList<OrdinaryFeaturePhoto>? ordinaryPhotos = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (editorSnapshot is not null
            && (submission is null || record.Status == RecordStatus.Draft
                || record.RecordId != editorSnapshot.RecordId || record.FormId != editorSnapshot.FormId
                || editorSnapshot.ServerBaseUrl != submission.ServerBaseUrl
                || editorSnapshot.ReplayPrincipal != submission.ReplayPrincipal))
        {
            throw new ArgumentException("The editor snapshot must belong to this submitted inspection.", nameof(editorSnapshot));
        }

        var entry = new FieldRecordEntry(record) { PublishedSubmission = submission, PublishedDraft = editorSnapshot };
        foreach (var photo in ordinaryPhotos ?? [])
        {
            entry.AddOrdinaryPhoto(photo);
        }

        if (record.Status != RecordStatus.Draft)
        {
            entry.MarkPending();
        }

        return await PersistAndTrackAsync(entry).ConfigureAwait(false);
    }

    /// <summary>Saves and tracks an unfinished published form under its existing record ID.</summary>
    public Task<FieldRecordEntry> SaveDraftAsync(FieldRecord record, PublishedFormDraft draft)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(draft);
        if (record.Status != RecordStatus.Draft || record.RecordId != draft.RecordId || record.FormId != draft.FormId)
        {
            throw new ArgumentException("The saved editor state must belong to this unfinished record.", nameof(draft));
        }

        var (_, restored) = draft.Open(record.FormId, record.RecordId, new Uri(draft.ServerBaseUrl));
        return PersistAndTrackAsync(new FieldRecordEntry(restored.Record) { PublishedDraft = draft });
    }

    private async Task<FieldRecordEntry> PersistAndTrackAsync(FieldRecordEntry entry)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveAsync(entry).ConfigureAwait(false);
            lock (_listGate)
            {
                var index = _entries.FindIndex(existing => existing.Record.RecordId == entry.Record.RecordId);
                if (index < 0)
                {
                    _entries.Insert(0, entry);
                }
                else
                {
                    _entries[index] = entry;
                }
            }

            return entry;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>Persists the current state of an entry (idempotent upsert).</summary>
    /// <param name="entry">The entry to persist.</param>
    public async Task SaveAsync(FieldRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await InitializeAsync().ConfigureAwait(false);
        await _store!.SaveAsync(entry).ConfigureAwait(false);
    }
}
