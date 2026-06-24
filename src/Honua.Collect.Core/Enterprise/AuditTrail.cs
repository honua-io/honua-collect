using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// The audit-log service (BACKLOG E3): the <see cref="IAuditSink"/> the app writes
/// to, backed by a durable <see cref="IAuditStore"/>. It assigns each event a
/// monotonic sequence and a tamper-evident chain hash (the same scheme as
/// <see cref="AuditLog"/>), scrubs secrets from detail strings, and exposes a
/// query/export API over the persisted trail.
/// </summary>
/// <remarks>
/// Appends are serialized so concurrent records can't race the sequence/head-hash
/// read-modify-write. A <see cref="TimeProvider"/> is injectable so event ordering
/// is deterministic in tests.
/// </remarks>
public sealed class AuditTrail : IAuditSink
{
    private readonly IAuditStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _appendGate = new(1, 1);

    /// <summary>Creates the trail over a durable store.</summary>
    /// <param name="store">Durable backing store (e.g. <see cref="SqliteAuditStore"/>).</param>
    /// <param name="clock">Time source; defaults to system.</param>
    public AuditTrail(IAuditStore store, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    DateTimeOffset IAuditSink.Now() => _clock.GetUtcNow();

    /// <inheritdoc />
    public AuditEntry Record(AuditEvent auditEvent)
        => RecordAsync(auditEvent).GetAwaiter().GetResult();

    /// <summary>Asynchronously records an event, persisting it to the durable store.</summary>
    /// <param name="auditEvent">Event to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entry.</returns>
    public async Task<AuditEntry> RecordAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var scrubbed = auditEvent with { Details = SecretScrubber.Scrub(auditEvent.Details) };

        await _appendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sequence = await _store.HeadSequenceAsync(ct).ConfigureAwait(false) + 1;
            var previousHash = await _store.HeadHashAsync(ct).ConfigureAwait(false);
            var hash = ComputeHash(sequence, scrubbed, previousHash);
            var entry = new AuditEntry(sequence, scrubbed, previousHash, hash);
            await _store.AppendAsync(entry, ct).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            _appendGate.Release();
        }
    }

    /// <summary>Queries the persisted trail.</summary>
    /// <param name="query">Filter, or null for the full trail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching entries, oldest first.</returns>
    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery? query = null, CancellationToken ct = default)
        => _store.QueryAsync(query, ct);

    /// <summary>
    /// Exports the matching trail as a newline-delimited JSON (JSONL) document — one
    /// entry per line, in sequence order — suitable for handing to a SIEM or an
    /// auditor. Sequence and chain hashes are included so integrity can be re-verified
    /// off-device.
    /// </summary>
    /// <param name="query">Filter, or null for the full trail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A JSONL export.</returns>
    public async Task<string> ExportJsonlAsync(AuditQuery? query = null, CancellationToken ct = default)
    {
        var entries = await _store.QueryAsync(query, ct).ConfigureAwait(false);
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(JsonSerializer.Serialize(new
            {
                seq = entry.Sequence,
                ts = entry.Event.TimestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                user = entry.Event.UserId,
                action = entry.Event.Action.ToString(),
                recordId = entry.Event.RecordId,
                details = entry.Event.Details,
                prevHash = entry.PreviousHash,
                hash = entry.Hash,
            }));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The canonical chain hash for an entry. Identical to <see cref="AuditLog"/>'s
    /// scheme so an exported trail verifies the same way regardless of which store
    /// produced it.
    /// </summary>
    /// <param name="sequence">Entry sequence.</param>
    /// <param name="e">The event.</param>
    /// <param name="previousHash">The prior entry's hash.</param>
    /// <returns>The lowercase hex SHA-256 chain hash.</returns>
    public static string ComputeHash(long sequence, AuditEvent e, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(e);
        var canonical = string.Join(
            '',
            sequence.ToString(CultureInfo.InvariantCulture),
            previousHash,
            e.TimestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            e.UserId,
            e.Action.ToString(),
            e.RecordId ?? string.Empty,
            e.Details ?? string.Empty);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Verifies the persisted chain: sequences are contiguous from 0, each entry's
    /// previous-hash links the prior entry, and each hash recomputes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the trail is intact.</returns>
    public async Task<bool> VerifyAsync(CancellationToken ct = default)
    {
        var entries = await _store.QueryAsync(null, ct).ConfigureAwait(false);
        var previousHash = string.Empty;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Sequence != i || entry.PreviousHash != previousHash)
            {
                return false;
            }

            if (ComputeHash(entry.Sequence, entry.Event, entry.PreviousHash) != entry.Hash)
            {
                return false;
            }

            previousHash = entry.Hash;
        }

        return true;
    }
}
