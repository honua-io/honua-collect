using System.Collections.ObjectModel;

namespace Honua.Collect.Core.Automation.Http;

/// <summary>
/// An HTTP request an automation rule wants performed, as a durable, offline-safe
/// unit of work (BACKLOG #44 — "HTTP request, queued offline"). It carries
/// everything needed to send the request later, without connectivity at author
/// time: the method, URL, headers, optional body, and a stable
/// <see cref="IdempotencyKey"/> so that replaying a queued request after a crash or
/// a flaky network is never a duplicate at the server.
/// </summary>
/// <remarks>
/// This is the request <em>shape</em> only — the queue/replay bookkeeping (attempt
/// count, next-attempt time, status, last error) lives on
/// <see cref="HttpOutboxEntry"/> so the immutable intent and the mutable delivery
/// state stay separate.
/// </remarks>
public sealed record HttpOutboxRequest
{
    private readonly IReadOnlyDictionary<string, string> _headers =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>HTTP method (e.g. <c>POST</c>, <c>PUT</c>, <c>GET</c>). Defaults to <c>POST</c>.</summary>
    public string Method { get; init; } = "POST";

    /// <summary>Destination URL.</summary>
    public required string Url { get; init; }

    /// <summary>Optional request body (sent as <c>application/json</c> unless a Content-Type header overrides it).</summary>
    public string? Body { get; init; }

    /// <summary>
    /// Request headers. An <c>Idempotency-Key</c> header is added automatically from
    /// <see cref="IdempotencyKey"/> by the transport, so callers need not set it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers
    {
        get => _headers;
        init => _headers = value ?? ReadOnlyDictionary<string, string>.Empty;
    }

    /// <summary>
    /// A stable key that makes a replay safe: the same logical request always carries
    /// the same key, so a server that honours idempotency collapses duplicates from a
    /// retry/replay into a single effect. Required.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}
