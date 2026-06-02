namespace Honua.Collect.Core.Enterprise;

/// <summary>Validity state of an authentication session.</summary>
public enum AuthSessionState
{
    /// <summary>Valid and not near expiry.</summary>
    Active,

    /// <summary>Still valid but within the refresh window.</summary>
    Expiring,

    /// <summary>Expired; the access token must not be used.</summary>
    Expired,
}

/// <summary>
/// An authenticated session established via enterprise SSO (OIDC/SAML — BACKLOG
/// E1). The interactive sign-in flow is the host's responsibility; this models
/// the resulting session — tokens, expiry, and granted scopes — and the
/// validity logic the app uses to decide when to refresh or re-authenticate.
/// </summary>
public sealed record AuthSession
{
    /// <summary>Authenticated user id (subject).</summary>
    public required string UserId { get; init; }

    /// <summary>Display name, when provided by the identity provider.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Opaque access token used for API calls.</summary>
    public required string AccessToken { get; init; }

    /// <summary>Refresh token, when the provider issues one.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>UTC time the access token expires.</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>Granted scopes / claims used for coarse client-side gating.</summary>
    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether the access token has expired as of the given time.</summary>
    /// <param name="asOfUtc">Reference time.</param>
    /// <returns><see langword="true"/> when expired.</returns>
    public bool IsExpired(DateTimeOffset asOfUtc) => asOfUtc >= ExpiresAtUtc;

    /// <summary>
    /// Whether the session should be proactively refreshed, i.e. it is within
    /// <paramref name="skew"/> of expiry.
    /// </summary>
    /// <param name="asOfUtc">Reference time.</param>
    /// <param name="skew">How far ahead of expiry to refresh.</param>
    /// <returns><see langword="true"/> when a refresh is due.</returns>
    public bool NeedsRefresh(DateTimeOffset asOfUtc, TimeSpan skew) => asOfUtc >= ExpiresAtUtc - skew;

    /// <summary>Whether a refresh is even possible (a refresh token is held).</summary>
    public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken);

    /// <summary>Whether the session grants a scope.</summary>
    /// <param name="scope">Scope to check.</param>
    /// <returns><see langword="true"/> when granted.</returns>
    public bool HasScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Scopes.Contains(scope);
    }

    /// <summary>Classifies the session state at a point in time.</summary>
    /// <param name="asOfUtc">Reference time.</param>
    /// <param name="refreshSkew">Window before expiry considered "expiring".</param>
    /// <returns>The session state.</returns>
    public AuthSessionState StateAt(DateTimeOffset asOfUtc, TimeSpan refreshSkew)
    {
        if (IsExpired(asOfUtc))
        {
            return AuthSessionState.Expired;
        }

        return NeedsRefresh(asOfUtc, refreshSkew) ? AuthSessionState.Expiring : AuthSessionState.Active;
    }
}
