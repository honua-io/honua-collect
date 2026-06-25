namespace Honua.Collect.Core.Automation.Http;

/// <summary>The delivery state of an HTTP request queued in the outbox.</summary>
public enum HttpOutboxStatus
{
    /// <summary>Waiting to be sent (or to be retried after a backoff).</summary>
    Pending,

    /// <summary>Delivered successfully (a 2xx response).</summary>
    Sent,

    /// <summary>
    /// Permanently failed — either a non-retryable response (a 4xx other than 408/429)
    /// or the retry budget was exhausted. It will not be attempted again.
    /// </summary>
    Failed,
}

/// <summary>
/// A queued HTTP request plus its replay bookkeeping (BACKLOG #44). Pairs the
/// immutable <see cref="HttpOutboxRequest"/> intent with the mutable delivery state
/// the <see cref="HttpRequestOutbox"/> advances: attempt count, when it next becomes
/// eligible (exponential backoff), terminal status, and the last error seen. The
/// entry is durable, so an app restart resumes exactly where it left off and a
/// half-sent request is never lost or silently duplicated.
/// </summary>
public sealed record HttpOutboxEntry
{
    /// <summary>Stable identifier for this queued request (defaults to a fresh GUID).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The request to send.</summary>
    public required HttpOutboxRequest Request { get; init; }

    /// <summary>Name of the automation rule that queued it (for diagnostics).</summary>
    public string? RuleName { get; init; }

    /// <summary>Current delivery status.</summary>
    public HttpOutboxStatus Status { get; init; } = HttpOutboxStatus.Pending;

    /// <summary>How many send attempts have been made.</summary>
    public int Attempts { get; init; }

    /// <summary>UTC time the entry was first enqueued.</summary>
    public DateTimeOffset EnqueuedAtUtc { get; init; }

    /// <summary>
    /// Earliest UTC time the entry may be attempted again. While
    /// <see cref="HttpOutboxStatus.Pending"/>, the dispatcher skips it until the clock
    /// passes this — that is the backoff. Initially equal to <see cref="EnqueuedAtUtc"/>.
    /// </summary>
    public DateTimeOffset NextAttemptUtc { get; init; }

    /// <summary>HTTP status code of the last completed attempt, if any.</summary>
    public int? LastStatusCode { get; init; }

    /// <summary>The last error/diagnostic message, if the most recent attempt failed.</summary>
    public string? LastError { get; init; }

    /// <summary>Whether this entry is eligible to send at <paramref name="now"/>.</summary>
    /// <param name="now">Current UTC time.</param>
    /// <returns><c>true</c> when pending and past its next-attempt time.</returns>
    public bool IsDue(DateTimeOffset now)
        => Status == HttpOutboxStatus.Pending && now >= NextAttemptUtc;
}
