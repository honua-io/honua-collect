using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App;

/// <summary>
/// Store of captured records — the source the Drafts/Outbox/Sent screen reads. A
/// submitted record enters the Outbox; once its server sync succeeds it moves to
/// Sent. Records are kept in memory for fast UI reads and written through to a
/// local SQLite database (<see cref="IRecordStore"/>) so Drafts/Outbox/Sent
/// survive app restarts and offline sessions.
/// </summary>
public static class CaptureStore
{
    private static readonly List<CollectRecordEntry> Entries = [];
    private static IRecordStore? _store;

    /// <summary>All tracked record entries, newest first.</summary>
    public static IReadOnlyList<CollectRecordEntry> All => Entries.AsReadOnly();

    /// <summary>
    /// Binds the persistent store and hydrates the in-memory list from it. Call
    /// once at startup before any screen reads <see cref="All"/>.
    /// </summary>
    /// <param name="store">The local record store to write through to.</param>
    public static async Task InitializeAsync(IRecordStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        var loaded = await store.LoadAllAsync().ConfigureAwait(false);
        Entries.Clear();
        // Newest first, matching AddSubmitted's insert-at-front ordering.
        foreach (var entry in loaded.OrderByDescending(e => e.Record.CreatedAtUtc))
        {
            Entries.Add(entry);
        }
    }

    /// <summary>Adds a freshly submitted record to the Outbox and returns its entry.</summary>
    /// <param name="record">The submitted record (review status past Draft).</param>
    /// <returns>The tracked entry.</returns>
    public static CollectRecordEntry AddSubmitted(FieldRecord record)
    {
        var entry = new CollectRecordEntry(record);
        if (record.Status != RecordStatus.Draft)
        {
            entry.MarkPending(); // enters the Outbox
        }

        Entries.Insert(0, entry);
        Save(entry);
        return entry;
    }

    /// <summary>
    /// Persists the current state of an entry (idempotent upsert). Call after any
    /// sync-state transition (<c>MarkUploading</c>/<c>MarkSynced</c>/<c>MarkFailed</c>)
    /// so the database mirrors the in-memory box.
    /// </summary>
    /// <param name="entry">The entry to persist.</param>
    public static async Task SaveAsync(CollectRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_store is not null)
        {
            await _store.SaveAsync(entry).ConfigureAwait(false);
        }
    }

    private static void Save(CollectRecordEntry entry) => _ = SaveAsync(entry);
}
