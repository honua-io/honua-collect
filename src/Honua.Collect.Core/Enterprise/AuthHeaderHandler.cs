using System.Net.Http.Headers;

namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Validates (and, when due, refreshes) the current session just before a request is
/// authenticated, returning the session to present or <see langword="null"/> when it
/// has lapsed unrecoverably. Wired to <see cref="AuthSessionManager.EnsureValidAsync"/>
/// so proactive refresh and fail-closed expiry fire on the real request path; kept as
/// a delegate so the Core transport doesn't depend on the manager.
/// </summary>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>The session to present, or null when re-authentication is required.</returns>
public delegate Task<AuthSession?> SessionValidator(CancellationToken cancellationToken);

/// <summary>
/// A <see cref="DelegatingHandler"/> that authenticates outbound requests from the
/// current <see cref="IAuthSessionStore"/>: a live (non-expired) session presents
/// its short-lived <b>bearer token</b> (<c>Authorization: Bearer</c>), obtained by
/// exchanging credentials at the server's token endpoint. When a
/// <see cref="SessionValidator"/> is supplied, the session is validated/refreshed
/// first, so a near-expiry token is renewed before use.
/// <para>
/// Fail-closed: once a session has been established, an <em>expired</em> token that
/// can't be refreshed is NOT silently downgraded to the anonymous fallback API key —
/// the request goes out with no credential, surfacing a 401 and the lifecycle's
/// re-sign-in prompt rather than quietly acting as an anonymous client. The fallback
/// key is only ever used when no session has been signed in (e.g. local dev). An
/// explicit header on the request is always left untouched.
/// </para>
/// </summary>
public sealed class AuthHeaderHandler : DelegatingHandler
{
    /// <summary>The legacy API-key header used for the signed-out fallback.</summary>
    public const string ApiKeyHeader = "X-API-Key";

    private readonly IAuthSessionStore _sessions;
    private readonly SessionValidator? _validate;
    private readonly TimeProvider _time;

    /// <summary>Creates the handler over the session store.</summary>
    /// <param name="sessions">The source of the current credential.</param>
    /// <param name="validate">
    /// Optional pre-request validate/refresh hook (typically
    /// <see cref="AuthSessionManager.EnsureValidAsync"/>); when null the handler reads
    /// the stored session directly without refreshing.
    /// </param>
    /// <param name="time">Clock for expiry checks (defaults to the system clock).</param>
    public AuthHeaderHandler(IAuthSessionStore sessions, SessionValidator? validate = null, TimeProvider? time = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _validate = validate;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // An explicit Authorization header always wins.
        if (request.Headers.Authorization is not null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Snapshot whether a session was ever signed in BEFORE validating: if the
        // validator expires and signs out a lapsed session, we must still fail closed
        // (no anonymous fallback) rather than treating it as a never-signed-in client.
        var hadSession = _sessions.Current is not null;

        // Validate/refresh the session just-in-time. A near-expiry token is renewed;
        // an unrecoverably expired one is signed out and yields null here.
        var session = _validate is not null
            ? await _validate(cancellationToken).ConfigureAwait(false)
            : _sessions.Current;

        if (session is { } live
            && !string.IsNullOrEmpty(live.AccessToken)
            && !live.IsExpired(_time.GetUtcNow()))
        {
            // Short-lived bearer token from the credential→token exchange.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", live.AccessToken);
        }
        else if (!hadSession
            && _sessions.FallbackApiKey is { Length: > 0 } key
            && !request.Headers.Contains(ApiKeyHeader))
        {
            // Fallback key is only for the never-signed-in case (e.g. local dev). A
            // session that lapsed fails closed: no silent downgrade to anonymous.
            request.Headers.Add(ApiKeyHeader, key);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
