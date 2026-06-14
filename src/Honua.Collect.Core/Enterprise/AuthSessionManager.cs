namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Attempts to refresh a session whose access token is at/near expiry, exchanging
/// its refresh token for a fresh session. Returns the refreshed session, or null
/// when the refresh is refused (which forces a re-authentication).
/// </summary>
/// <remarks>
/// Injected so the lifecycle is testable without a live server, and because the
/// wire contract depends on server support: the current token endpoint
/// (<c>generateToken</c>) issues no refresh token, so no refresher is wired in
/// production yet. <see cref="AuthSessionManager"/> simply skips the refresh step
/// when no refresher is supplied or the session holds no refresh token.
/// </remarks>
/// <param name="expiring">The session that is at/near expiry.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>A refreshed session, or null when refresh is unavailable/refused.</returns>
public delegate Task<AuthSession?> SessionRefresher(AuthSession expiring, CancellationToken cancellationToken);

/// <summary>
/// Owns the auth session lifecycle on top of <see cref="IAuthSessionStore"/>:
/// <list type="bullet">
/// <item>resumes a persisted session on startup, honoring expiry (a stale token is
/// discarded, never presented);</item>
/// <item>persists a sign-in to platform secure storage only (never the record DB or
/// plain config), so the at-rest posture is unchanged;</item>
/// <item>proactively refreshes a near-expiry session when a <see cref="SessionRefresher"/>
/// and a refresh token are both available;</item>
/// <item>raises <see cref="SessionExpired"/> when a session lapses unrecoverably, so
/// the host can prompt a graceful re-sign-in instead of failing silently.</item>
/// </list>
/// </summary>
public sealed class AuthSessionManager
{
    private readonly IAuthSessionStore _store;
    private readonly ISessionPersistence _persistence;
    private readonly SessionRefresher? _refresher;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshSkew;

    /// <summary>Creates the lifecycle manager.</summary>
    /// <param name="store">The shared session store the transport reads from.</param>
    /// <param name="persistence">Secure-storage persistence for cross-restart resume.</param>
    /// <param name="refresher">Optional refresh-token exchange; null when the server issues no refresh token.</param>
    /// <param name="clock">Time source (defaults to system); injectable for tests.</param>
    /// <param name="refreshSkew">How far ahead of expiry to refresh proactively (default 5 minutes).</param>
    public AuthSessionManager(
        IAuthSessionStore store,
        ISessionPersistence persistence,
        SessionRefresher? refresher = null,
        TimeProvider? clock = null,
        TimeSpan? refreshSkew = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _refresher = refresher;
        _clock = clock ?? TimeProvider.System;
        _refreshSkew = refreshSkew ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Raised when a session has expired and could not be refreshed.</summary>
    public event EventHandler? SessionExpired;

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// Restores a persisted session on startup. A session that has already expired
    /// (or fails to load) is discarded — never presented — and the store is left
    /// signed out.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resumed live session, or null when none is available.</returns>
    public async Task<AuthSession?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return null;
        }

        if (saved.IsExpired(Now))
        {
            // Honor expiry on load: drop the stale token rather than resume it.
            await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        _store.Set(saved);
        return saved;
    }

    /// <summary>Establishes a session: makes it live and persists it to secure storage.</summary>
    /// <param name="session">The newly authenticated session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SignInAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        _store.Set(session);
        await _persistence.SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears the live session and removes any persisted copy.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _store.Set(null);
        await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the current session, proactively refreshing it first when it is near
    /// expiry and a refresh is possible. A session that is merely <em>expiring</em>
    /// but couldn't be refreshed early is still returned (it remains valid until it
    /// actually expires). A session that has <em>expired</em> and cannot be refreshed
    /// signs out, raises <see cref="SessionExpired"/>, and returns null.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A usable session, or null when re-authentication is required.</returns>
    public async Task<AuthSession?> EnsureValidAsync(CancellationToken cancellationToken = default)
    {
        var current = _store.Current;
        if (current is null)
        {
            return null;
        }

        var state = current.StateAt(Now, _refreshSkew);
        if (state == AuthSessionState.Active)
        {
            return current;
        }

        // Expiring or expired: try a refresh when both a refresher and a token exist.
        if (_refresher is not null && current.CanRefresh)
        {
            AuthSession? refreshed;
            try
            {
                refreshed = await _refresher(current, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed refresh is not fatal on its own — fall through to the
                // expiry decision below (cancellation propagates intentionally).
                refreshed = null;
            }

            if (refreshed is not null && !refreshed.IsExpired(Now))
            {
                await SignInAsync(refreshed, cancellationToken).ConfigureAwait(false);
                return refreshed;
            }
        }

        if (state == AuthSessionState.Expiring)
        {
            // Still within the access token's life; keep using it until it expires.
            return current;
        }

        // Expired and unrecoverable: surface a graceful re-sign-in.
        await SignOutAsync(cancellationToken).ConfigureAwait(false);
        SessionExpired?.Invoke(this, EventArgs.Empty);
        return null;
    }
}
