using System.Net.Http.Headers;

namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// A <see cref="DelegatingHandler"/> that authenticates outbound requests from the
/// current <see cref="IAuthSessionStore"/>: a live (non-expired) session presents
/// its short-lived <b>bearer token</b> (<c>Authorization: Bearer</c>), obtained by
/// exchanging credentials at the server's token endpoint. When signed out, an
/// optional fallback API key is sent as <c>X-API-Key</c>. An expired session is
/// not presented (re-auth required), and an explicit header on the request is left
/// untouched.
/// </summary>
public sealed class AuthHeaderHandler : DelegatingHandler
{
    /// <summary>The legacy API-key header used for the signed-out fallback.</summary>
    public const string ApiKeyHeader = "X-API-Key";

    private readonly IAuthSessionStore _sessions;
    private readonly TimeProvider _time;

    /// <summary>Creates the handler over the session store.</summary>
    /// <param name="sessions">The source of the current credential.</param>
    /// <param name="time">Clock for expiry checks (defaults to the system clock).</param>
    public AuthHeaderHandler(IAuthSessionStore sessions, TimeProvider? time = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = _sessions.Current;
        if (session is { } live
            && !string.IsNullOrEmpty(live.AccessToken)
            && !live.IsExpired(_time.GetUtcNow())
            && request.Headers.Authorization is null)
        {
            // Short-lived bearer token from the credential→token exchange.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", live.AccessToken);
        }
        else if (_sessions.FallbackApiKey is { Length: > 0 } key && !request.Headers.Contains(ApiKeyHeader))
        {
            request.Headers.Add(ApiKeyHeader, key);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
