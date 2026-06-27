namespace Honua.Collect.Core.Automation.Http;

/// <summary>The retry/backoff policy the outbox applies to a transient failure.</summary>
/// <param name="MaxAttempts">Total send attempts before an entry is marked permanently failed.</param>
/// <param name="BaseDelay">First backoff delay; doubles each attempt (capped at <paramref name="MaxDelay"/>).</param>
/// <param name="MaxDelay">Upper bound on the backoff delay.</param>
public readonly record struct HttpOutboxRetryPolicy(
    int MaxAttempts,
    TimeSpan BaseDelay,
    TimeSpan MaxDelay)
{
    /// <summary>The default policy: 5 attempts, 30s base delay, doubling, capped at 1h.</summary>
    public static HttpOutboxRetryPolicy Default { get; } =
        new(MaxAttempts: 5, BaseDelay: TimeSpan.FromSeconds(30), MaxDelay: TimeSpan.FromHours(1));

    /// <summary>The backoff delay before the attempt with the given (1-based) number.</summary>
    /// <param name="attempt">The attempt number that just failed (1 = first).</param>
    /// <returns>How long to wait before the next attempt.</returns>
    public TimeSpan DelayForAttempt(int attempt)
    {
        // Exponential: base * 2^(attempt-1), capped. Guard the shift so a large
        // attempt count cannot overflow into a negative/garbage delay.
        var exponent = Math.Min(attempt - 1, 30);
        var scaled = BaseDelay.Ticks * (1L << exponent);
        var ticks = Math.Min(scaled, MaxDelay.Ticks);
        return TimeSpan.FromTicks(Math.Max(ticks, BaseDelay.Ticks));
    }
}

/// <summary>
/// The durable HTTP outbox (BACKLOG #44 — "HTTP request, queued offline; replays on
/// sync"). Automation rules never send over the network; instead the host
/// <see cref="EnqueueAsync(HttpOutboxRequest, string?, CancellationToken)"/>s the
/// request here, and a connectivity-triggered <see cref="DrainAsync"/> replays the
/// pending queue through the injected <see cref="IHttpRequestTransport"/>. Each
/// entry carries an idempotency key (so a replay is never a duplicate), an attempt
/// count, and an exponential-backoff next-attempt time, and ends up
/// <see cref="HttpOutboxStatus.Sent"/> or permanently <see cref="HttpOutboxStatus.Failed"/>.
/// </summary>
/// <remarks>
/// Everything is offline-deterministic: a <see cref="TimeProvider"/> drives both the
/// enqueue timestamps and the backoff schedule, so tests advance a fake clock to
/// prove a queued request waits, then replays once connectivity returns — no live
/// network, no real delay.
/// </remarks>
public sealed class HttpRequestOutbox
{
    private readonly IHttpOutboxStore _store;
    private readonly IHttpRequestTransport _transport;
    private readonly TimeProvider _clock;
    private readonly HttpOutboxRetryPolicy _policy;

    /// <summary>Creates the outbox.</summary>
    /// <param name="store">Durable entry store.</param>
    /// <param name="transport">The network seam requests are sent through.</param>
    /// <param name="clock">Time source (defaults to system); injectable for tests.</param>
    /// <param name="policy">Optional retry/backoff policy (defaults to <see cref="HttpOutboxRetryPolicy.Default"/>).</param>
    public HttpRequestOutbox(
        IHttpOutboxStore store,
        IHttpRequestTransport transport,
        TimeProvider? clock = null,
        HttpOutboxRetryPolicy? policy = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? TimeProvider.System;
        _policy = policy ?? HttpOutboxRetryPolicy.Default;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// Enqueues a request for delivery. If a pending entry with the same idempotency
    /// key is already queued, that existing entry is returned unchanged (so a rule
    /// firing twice does not queue a duplicate). The entry is durably persisted and
    /// eligible to send immediately on the next <see cref="DrainAsync"/>.
    /// </summary>
    /// <param name="request">The request to queue.</param>
    /// <param name="ruleName">Optional name of the rule that queued it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The queued (or pre-existing) entry.</returns>
    public async Task<HttpOutboxEntry> EnqueueAsync(
        HttpOutboxRequest request,
        string? ruleName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        var existing = await _store.FindByIdempotencyKeyAsync(request.IdempotencyKey, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = Now;
        var entry = new HttpOutboxEntry
        {
            Request = request,
            RuleName = ruleName,
            Status = HttpOutboxStatus.Pending,
            Attempts = 0,
            EnqueuedAtUtc = now,
            NextAttemptUtc = now,
        };

        await _store.SaveAsync(entry, ct).ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    /// Folds every <see cref="AutomationResult.HttpRequests"/> from an automation run
    /// into the outbox — the bridge the host calls after running rules so queued HTTP
    /// becomes durable work.
    /// </summary>
    /// <param name="result">The automation run result.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries newly enqueued (duplicates by key are skipped).</returns>
    public async Task<int> EnqueueFromAsync(AutomationResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var enqueued = 0;
        foreach (var queued in result.HttpRequests)
        {
            var before = await _store.FindByIdempotencyKeyAsync(queued.Request.IdempotencyKey, ct).ConfigureAwait(false);
            await EnqueueAsync(queued.Request, queued.RuleName, ct).ConfigureAwait(false);
            if (before is null)
            {
                enqueued++;
            }
        }

        return enqueued;
    }

    /// <summary>
    /// Drains the queue: every entry that is pending and past its backoff is sent
    /// through the transport. On success the entry becomes <see cref="HttpOutboxStatus.Sent"/>;
    /// on a retryable failure its attempt count and next-attempt time advance (or it
    /// fails permanently once the retry budget is spent); on a non-retryable failure
    /// it fails immediately. Call this whenever connectivity returns / on a sync tick.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A summary of what happened in this drain.</returns>
    public async Task<HttpDrainResult> DrainAsync(CancellationToken ct = default)
    {
        // Query only the due Pending rows (indexed) rather than reloading and sorting
        // the whole table — terminal Sent/Failed rows accumulate over the app's life
        // and must not be rescanned on every connectivity tick.
        var entries = await _store.LoadDueAsync(Now, ct).ConfigureAwait(false);
        var sent = 0;
        var retried = 0;
        var failed = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await AttemptAsync(entry, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case AttemptOutcome.Sent:
                    sent++;
                    break;
                case AttemptOutcome.Retry:
                    retried++;
                    break;
                case AttemptOutcome.Failed:
                    failed++;
                    break;
            }
        }

        return new HttpDrainResult(sent, retried, failed);
    }

    /// <summary>
    /// Purges terminal (delivered or permanently-failed) entries from the durable
    /// store so the outbox table does not grow without bound over the app's life. Safe
    /// to call after a drain or on a maintenance tick; terminal entries are never
    /// retried, so dropping them loses no pending work.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries purged.</returns>
    public Task<int> PurgeTerminalAsync(CancellationToken ct = default)
        => _store.PurgeTerminalAsync(ct);

    private async Task<AttemptOutcome> AttemptAsync(HttpOutboxEntry entry, CancellationToken ct)
    {
        HttpSendResult result;
        try
        {
            result = await _transport.SendAsync(entry.Request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A throwing transport is treated as a transient failure (offline-like),
            // so the outbox never loses an entry to an unexpected exception.
            result = HttpSendResult.Offline(ex.Message);
        }

        var attempts = entry.Attempts + 1;

        if (result.IsSuccess)
        {
            await _store.SaveAsync(entry with
            {
                Status = HttpOutboxStatus.Sent,
                Attempts = attempts,
                LastStatusCode = result.StatusCode,
                LastError = null,
            }, ct).ConfigureAwait(false);
            return AttemptOutcome.Sent;
        }

        var exhausted = attempts >= _policy.MaxAttempts;
        if (!result.IsRetryable || exhausted)
        {
            await _store.SaveAsync(entry with
            {
                Status = HttpOutboxStatus.Failed,
                Attempts = attempts,
                LastStatusCode = result.StatusCode,
                LastError = result.Error ?? (exhausted ? "Retry budget exhausted" : "Non-retryable response"),
            }, ct).ConfigureAwait(false);
            return AttemptOutcome.Failed;
        }

        await _store.SaveAsync(entry with
        {
            Status = HttpOutboxStatus.Pending,
            Attempts = attempts,
            NextAttemptUtc = Now + _policy.DelayForAttempt(attempts),
            LastStatusCode = result.StatusCode,
            LastError = result.Error,
        }, ct).ConfigureAwait(false);
        return AttemptOutcome.Retry;
    }

    private enum AttemptOutcome
    {
        Sent,
        Retry,
        Failed,
    }
}

/// <summary>A summary of one <see cref="HttpRequestOutbox.DrainAsync"/> pass.</summary>
/// <param name="Sent">Entries delivered this drain.</param>
/// <param name="Retried">Entries that failed transiently and were rescheduled.</param>
/// <param name="Failed">Entries that failed permanently this drain.</param>
public readonly record struct HttpDrainResult(int Sent, int Retried, int Failed)
{
    /// <summary>Total entries acted on this drain.</summary>
    public int Total => Sent + Retried + Failed;
}
