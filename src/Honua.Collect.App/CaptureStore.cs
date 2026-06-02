using Honua.Collect.Core.Records;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App;

/// <summary>
/// In-memory store of captured records for the demo app — the source the
/// Drafts/Outbox/Sent screen reads. A submitted record enters the Outbox; once
/// its server sync succeeds it moves to Sent. (Production persists to GeoPackage;
/// this keeps the sample self-contained.)
/// </summary>
public static class CaptureStore
{
    private static readonly List<CollectRecordEntry> Entries = [];

    /// <summary>All tracked record entries, newest first.</summary>
    public static IReadOnlyList<CollectRecordEntry> All => Entries.AsReadOnly();

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
        return entry;
    }
}
