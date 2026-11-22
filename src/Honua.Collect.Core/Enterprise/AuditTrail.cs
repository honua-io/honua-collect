// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.Collect.Core.Enterprise;

public sealed class AuditTrail : IAuditSink
{
    private readonly IAuditStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _appendGate = new(1, 1);

    public AuditTrail(IAuditStore store, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    DateTimeOffset IAuditSink.Now() => _clock.GetUtcNow();

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

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery? query = null, CancellationToken ct = default)
        => _store.QueryAsync(query, ct);

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
