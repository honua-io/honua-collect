// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public enum AuditAction
{
    RecordCreated,

    RecordEdited,

    RecordSubmitted,

    RecordApproved,

    RecordRejected,

    RecordDeleted,

    DataExported,

    SignIn,

    SignOut,

    SessionRefreshed,

    SessionExpired,

    SyncPush,

    SyncPull,

    PermissionDenied,
}

public sealed record AuditEvent(DateTimeOffset TimestampUtc, string UserId, AuditAction Action, string? RecordId = null, string? Details = null);

public sealed record AuditEntry(long Sequence, AuditEvent Event, string PreviousHash, string Hash);

public sealed class AuditLog
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries => _entries;

    public string HeadHash => _entries.Count == 0 ? string.Empty : _entries[^1].Hash;

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

    public IReadOnlyList<AuditEntry> ForRecord(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return _entries.Where(e => string.Equals(e.Event.RecordId, recordId, StringComparison.Ordinal)).ToList();
    }

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
