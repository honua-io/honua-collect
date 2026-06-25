namespace Honua.Collect.Core.Automation.Http;

/// <summary>
/// The outcome of a transport send: the HTTP status code (when the request reached
/// the server) and an optional diagnostic message. A null <see cref="StatusCode"/>
/// means the request never got a response (offline, DNS failure, timeout) — a
/// transient/retryable condition.
/// </summary>
/// <param name="StatusCode">HTTP status code, or null when no response was received.</param>
/// <param name="Error">Optional diagnostic message (set when not a success).</param>
public readonly record struct HttpSendResult(int? StatusCode, string? Error = null)
{
    /// <summary>A 2xx response — the request was delivered.</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>
    /// Whether a failed send should be retried. No response at all (offline/timeout)
    /// is retryable, as are 408 (timeout), 429 (rate-limited), and any 5xx; other 4xx
    /// are client errors that will never succeed on replay, so they fail permanently.
    /// </summary>
    public bool IsRetryable => StatusCode switch
    {
        null => true,
        408 or 429 => true,
        >= 500 => true,
        _ => false,
    };

    /// <summary>A "no connectivity" result (no status code) with an optional message.</summary>
    public static HttpSendResult Offline(string? error = "No connectivity") => new(null, error);

    /// <summary>A successful result for the given 2xx status code.</summary>
    public static HttpSendResult Ok(int statusCode = 200) => new(statusCode);
}

/// <summary>
/// The network seam the HTTP outbox sends through (BACKLOG #44). Production binds it
/// to an <c>HttpClient</c>; tests bind a fake that can simulate offline, transient
/// failures, and success without any live network. Keeping the runtime/outbox off
/// the concrete network type is what makes the queue fully deterministic and
/// offline-testable.
/// </summary>
public interface IHttpRequestTransport
{
    /// <summary>
    /// Attempts to send a queued request. Implementations should NOT throw for a
    /// network failure — return <see cref="HttpSendResult.Offline"/> (or a non-2xx
    /// status) instead, so the outbox can apply its retry policy. The idempotency key
    /// must be sent (e.g. as an <c>Idempotency-Key</c> header) so replays de-dupe.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The send result.</returns>
    Task<HttpSendResult> SendAsync(HttpOutboxRequest request, CancellationToken ct = default);
}
