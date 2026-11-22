// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public sealed record AuditQuery(
    string? UserId = null,
    AuditAction? Action = null,
    DateTimeOffset? SinceUtc = null,
    DateTimeOffset? UntilUtc = null)
{
    public bool Matches(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var e = entry.Event;
        if (UserId is not null && !string.Equals(e.UserId, UserId, StringComparison.Ordinal))
        {
            return false;
        }

        if (Action is { } action && e.Action != action)
        {
            return false;
        }

        if (SinceUtc is { } since && e.TimestampUtc < since)
        {
            return false;
        }

        if (UntilUtc is { } until && e.TimestampUtc >= until)
        {
            return false;
        }

        return true;
    }
}

public interface IAuditStore
{
    Task<long> HeadSequenceAsync(CancellationToken ct = default);

    Task<string> HeadHashAsync(CancellationToken ct = default);

    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery? query = null, CancellationToken ct = default);
}
