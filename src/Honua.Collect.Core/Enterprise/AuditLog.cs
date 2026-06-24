namespace Honua.Collect.Core.Enterprise;

/// <summary>Auditable actions recorded on the device (BACKLOG E3).</summary>
public enum AuditAction
{
    /// <summary>A record was created.</summary>
    RecordCreated,

    /// <summary>A record was edited.</summary>
    RecordEdited,

    /// <summary>A record was submitted.</summary>
    RecordSubmitted,

    /// <summary>A record was approved.</summary>
    RecordApproved,

    /// <summary>A record was rejected.</summary>
    RecordRejected,

    /// <summary>A record was deleted.</summary>
    RecordDeleted,

    /// <summary>Data was exported.</summary>
    DataExported,

    /// <summary>A user signed in.</summary>
    SignIn,

    /// <summary>A user signed out.</summary>
    SignOut,

    /// <summary>An auth session was refreshed.</summary>
    SessionRefreshed,

    /// <summary>An auth session expired and could not be recovered.</summary>
    SessionExpired,

    /// <summary>Local records were pushed to the server.</summary>
    SyncPush,

    /// <summary>Remote features were pulled to the device.</summary>
    SyncPull,

    /// <summary>An action was attempted without sufficient permission.</summary>
    PermissionDenied,
}

/// <summary>A single audited action.</summary>
/// <param name="TimestampUtc">When the action occurred.</param>
/// <param name="UserId">Who performed it.</param>
/// <param name="Action">What was done.</param>
/// <param name="RecordId">Affected record, when applicable.</param>
/// <param name="Details">Optional human-readable detail.</param>
public sealed record AuditEvent(DateTimeOffset TimestampUtc, string UserId, AuditAction Action, string? RecordId = null, string? Details = null);

/// <summary>An entry in the audit chain: the event plus its hash linkage.</summary>
/// <param name="Sequence">Zero-based position in the chain.</param>
/// <param name="Event">The audited event.</param>
/// <param name="PreviousHash">Hash of the prior entry (empty for the first).</param>
/// <param name="Hash">Hash of this entry, chaining <paramref name="PreviousHash"/> and the event.</param>
public sealed record AuditEntry(long Sequence, AuditEvent Event, string PreviousHash, string Hash);

/// <summary>
/// An append-only, tamper-evident audit log (BACKLOG E3). Each entry's hash
/// chains the previous hash with the event content, so any later modification or
/// deletion of an earlier entry breaks the chain and is detectable via
/// <see cref="Verify"/>. Entries can only be appended, never changed.
/// </summary>
public sealed class AuditLog
{
    private readonly List<AuditEntry> _entries = [];

    /// <summary>All entries, in order.</summary>
    public IReadOnlyList<AuditEntry> Entries => _entries;

    /// <summary>The hash of the most recent entry, or empty when the log is empty.</summary>
    public string HeadHash => _entries.Count == 0 ? string.Empty : _entries[^1].Hash;

    /// <summary>Appends an event to the log and returns the new entry.</summary>
    /// <param name="auditEvent">Event to record.</param>
    /// <returns>The appended entry.</returns>
    public AuditEntry Append(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Defence in depth: scrub any secret that slipped into the detail string
        // before it is hashed into the durable, tamper-evident chain.
        auditEvent = auditEvent with { Details = SecretScrubber.Scrub(auditEvent.Details) };

        var sequence = _entries.Count;
        var previousHash = HeadHash;
        var hash = AuditTrail.ComputeHash(sequence, auditEvent, previousHash);
        var entry = new AuditEntry(sequence, auditEvent, previousHash, hash);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Entries affecting a specific record, in order.</summary>
    /// <param name="recordId">Record id.</param>
    /// <returns>Matching entries.</returns>
    public IReadOnlyList<AuditEntry> ForRecord(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return _entries.Where(e => string.Equals(e.Event.RecordId, recordId, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// Verifies the integrity of the chain: every entry's hash must recompute and
    /// its previous-hash must match the prior entry.
    /// </summary>
    /// <returns><see langword="true"/> when the chain is intact.</returns>
    public bool Verify()
    {
        var previousHash = string.Empty;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Sequence != i || entry.PreviousHash != previousHash)
            {
                return false;
            }

            if (AuditTrail.ComputeHash(entry.Sequence, entry.Event, entry.PreviousHash) != entry.Hash)
            {
                return false;
            }

            previousHash = entry.Hash;
        }

        return true;
    }
}
