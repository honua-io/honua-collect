// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Field.Core.Enterprise;

/// <summary>
/// Attempts to refresh a session whose access token is at/near expiry, exchanging
/// its refresh token for a fresh session. Returns the refreshed session, or null
/// when the refresh is refused (which forces a re-authentication).
/// </summary>
/// <remarks>
/// The host supplies a refresher for sessions that carry a refresh token.
/// Sessions issued without one require sign-in again after expiry.
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
    private readonly ILogger<AuthSessionManager> _logger;

    // Serializes token refresh so a burst of near-expiry requests does not all call
    // the refresh endpoint at once and rotate refresh tokens that mutually invalidate
    // each other into 401s (AUD-255). The first caller refreshes; the rest wait, then
    // reuse the freshly stored session instead of refreshing again.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _sessionWrites = new(1, 1);

    /// <summary>Creates the lifecycle manager.</summary>
    /// <param name="store">The shared session store the transport reads from.</param>
    /// <param name="persistence">Secure-storage persistence for cross-restart resume.</param>
    /// <param name="refresher">Optional refresh-token exchange; null when the server issues no refresh token.</param>
    /// <param name="clock">Time source (defaults to system); injectable for tests.</param>
    /// <param name="refreshSkew">How far ahead of expiry to refresh proactively (default 5 minutes).</param>
    /// <param name="logger">Optional structured logger.</param>
    public AuthSessionManager(
        IAuthSessionStore store,
        ISessionPersistence persistence,
        SessionRefresher? refresher = null,
        TimeProvider? clock = null,
        TimeSpan? refreshSkew = null,
        ILogger<AuthSessionManager>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _refresher = refresher;
        _clock = clock ?? TimeProvider.System;
        _refreshSkew = refreshSkew ?? TimeSpan.FromMinutes(5);
        _logger = logger ?? NullLogger<AuthSessionManager>.Instance;
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
        await _sessionWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (saved is null)
            {
                return null;
            }

            if (saved.IsExpired(Now))
            {
                // Honor expiry on load: drop the stale token rather than resume it.
                _logger.LogInformation("Discarded expired persisted session for user {UserId}.", saved.UserId);
                await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            _store.Set(saved);
            _logger.LogInformation("Restored persisted session for user {UserId}.", saved.UserId);
            return saved;
        }
        finally
        {
            _sessionWrites.Release();
        }
    }

    /// <summary>Establishes a session: makes it live and persists it to secure storage.</summary>
    /// <param name="session">The newly authenticated session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SignInAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await WriteSessionAsync(session, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Signed in user {UserId}.", session.UserId);
    }

    /// <summary>Clears the live session and removes any persisted copy.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var previousUser = _store.Current?.UserId;
        await WriteSessionAsync(null, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Signed out user {UserId}.", previousUser ?? "(none)");
    }

    private async Task<bool> WriteSessionAsync(
        AuthSession? next, CancellationToken cancellationToken, AuthSession? expected = null)
    {
        await _sessionWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expected is not null && !ReferenceEquals(expected, _store.Current))
            {
                return false;
            }

            _store.Set(next);
            if (next is null)
            {
                await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _persistence.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            _sessionWrites.Release();
        }
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

        if (current.StateAt(Now, _refreshSkew) == AuthSessionState.Active)
        {
            return current;
        }

        // Expiring or expired: try a single-flighted refresh when both a refresher and
        // a token exist. The refresh is serialized so concurrent near-expiry callers
        // don't stampede the endpoint and rotate tokens into mutual 401s.
        if (_refresher is not null && current.CanRefresh)
        {
            var refreshed = await RefreshSingleFlightAsync(current, cancellationToken).ConfigureAwait(false);
            if (refreshed is not null)
            {
                return refreshed;
            }
        }

        // Re-read: a concurrent caller may have refreshed the stored session while we
        // were deciding, so honor the freshest state.
        var latest = _store.Current;
        if (latest is null || !string.Equals(current.UserId, latest.UserId, StringComparison.Ordinal))
        {
            return null;
        }
        current = latest;
        var state = current.StateAt(Now, _refreshSkew);
        if (state is AuthSessionState.Active or AuthSessionState.Expiring)
        {
            // Active (someone else just refreshed) or still within the token's life:
            // keep using it until it actually expires.
            return current;
        }

        // Expired and unrecoverable: surface a graceful re-sign-in.
        _logger.LogWarning("Auth session expired and could not be refreshed for user {UserId}; signing out.", current.UserId);
        if (await WriteSessionAsync(null, cancellationToken, current).ConfigureAwait(false))
        {
            SessionExpired?.Invoke(this, EventArgs.Empty);
        }
        return null;
    }

    /// <summary>
    /// Refreshes the session under a single-flight lock: only one refresh runs at a
    /// time, and a caller that finds the stored session already refreshed (Active)
    /// while it waited reuses it rather than calling the endpoint again.
    /// </summary>
    /// <param name="seen">The expiring/expired session the caller observed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A usable refreshed session, or null when refresh was unavailable/refused.</returns>
    private async Task<AuthSession?> RefreshSingleFlightAsync(AuthSession seen, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check: another caller may have refreshed while we held in the queue.
            var current = _store.Current;
            if (current is null || !string.Equals(current.UserId, seen.UserId, StringComparison.Ordinal))
            {
                return null;
            }
            if (current.StateAt(Now, _refreshSkew) == AuthSessionState.Active)
            {
                return current;
            }

            if (_refresher is null || !current.CanRefresh)
            {
                return null;
            }

            AuthSession? refreshed;
            try
            {
                refreshed = await _refresher(current, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed refresh is not fatal on its own — the caller falls through
                // to the expiry decision (cancellation propagates intentionally).
                _logger.LogWarning(
                    ex,
                    "Session refresh threw {ExceptionType} for user {UserId}.",
                    ex.GetType().Name, current.UserId);
                refreshed = null;
            }

            if (refreshed is not null && !refreshed.IsExpired(Now)
                && string.Equals(refreshed.UserId, current.UserId, StringComparison.Ordinal)
                && await WriteSessionAsync(refreshed, cancellationToken, current).ConfigureAwait(false))
            {
                _logger.LogInformation("Refreshed auth session for user {UserId}.", refreshed.UserId);
                return refreshed;
            }

            _logger.LogWarning("Session refresh returned no usable session for user {UserId}.", current.UserId);
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
