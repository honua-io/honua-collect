// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;

namespace Honua.Collect.Core.Enterprise;

public delegate Task<AuthSession?> SessionValidator(CancellationToken cancellationToken);

public sealed class AuthHeaderHandler : DelegatingHandler
{
    public const string ApiKeyHeader = "X-API-Key";

    private readonly IAuthSessionStore _sessions;
    private readonly SessionValidator? _validate;
    private readonly TimeProvider _time;

    public AuthHeaderHandler(IAuthSessionStore sessions, SessionValidator? validate = null, TimeProvider? time = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _validate = validate;
        _time = time ?? TimeProvider.System;
    }

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
