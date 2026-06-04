namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Holds the signed-in <see cref="AuthSession"/> for the running app. This is the
/// seam that makes login functional: the transport layer
/// (<see cref="AuthHeaderHandler"/>) reads the current session and presents its
/// bearer token on every request. When signed out, an optional fallback API key
/// (e.g. a local-dev credential) is used instead.
/// </summary>
public interface IAuthSessionStore
{
    /// <summary>The current authenticated session, or null when signed out.</summary>
    AuthSession? Current { get; }

    /// <summary>
    /// Credential presented when no live session exists (e.g. a dev API key); null
    /// in production, where sign-in is required.
    /// </summary>
    string? FallbackApiKey { get; }

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
    private AuthSession? _session;

    /// <summary>Creates the store with an optional fallback credential for the signed-out state.</summary>
    /// <param name="fallbackApiKey">Credential to present when no session is set (e.g. a demo key); null for none.</param>
    public AuthSessionStore(string? fallbackApiKey = null) => FallbackApiKey = fallbackApiKey;

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
    public string? FallbackApiKey { get; }

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
