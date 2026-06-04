namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Holds the signed-in <see cref="AuthSession"/> for the running app and exposes
/// the credential that outbound requests should present. This is the seam that
/// makes login functional rather than cosmetic: the transport layer
/// (<see cref="AuthHeaderHandler"/>) reads <see cref="CurrentApiKey"/> so every
/// sync/upload request carries the authenticated user's token.
/// </summary>
public interface IAuthSessionStore
{
    /// <summary>The current authenticated session, or null when signed out.</summary>
    AuthSession? Current { get; }

    /// <summary>
    /// The credential to send on requests: the current session's access token, or
    /// the configured fallback when not signed in (so an offline demo still works).
    /// </summary>
    string? CurrentApiKey { get; }

    /// <summary>Sets (or clears, with null) the current session.</summary>
    /// <param name="session">The new session, or null to sign out.</param>
    void Set(AuthSession? session);

    /// <summary>Raised when the current session changes.</summary>
    event EventHandler? Changed;
}

/// <summary>Thread-safe <see cref="IAuthSessionStore"/>.</summary>
public sealed class AuthSessionStore : IAuthSessionStore
{
    private readonly object _gate = new();
    private readonly string? _fallbackApiKey;
    private AuthSession? _session;

    /// <summary>Creates the store with an optional fallback credential for the signed-out state.</summary>
    /// <param name="fallbackApiKey">Credential to present when no session is set (e.g. a demo key); null for none.</param>
    public AuthSessionStore(string? fallbackApiKey = null) => _fallbackApiKey = fallbackApiKey;

    /// <inheritdoc />
    public AuthSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <inheritdoc />
    public string? CurrentApiKey
    {
        get
        {
            lock (_gate)
            {
                // An expired session is not presented — fall back (re-auth required)
                // rather than sending a stale credential.
                if (_session is { } session && !session.IsExpired(DateTimeOffset.UtcNow))
                {
                    return session.AccessToken;
                }

                return _fallbackApiKey;
            }
        }
    }

    /// <inheritdoc />
    public void Set(AuthSession? session)
    {
        lock (_gate)
        {
            _session = session;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;
}
