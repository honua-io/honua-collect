// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Collect.Core.Enterprise;

public delegate Task<AuthSession?> SessionRefresher(AuthSession expiring, CancellationToken cancellationToken);

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

    public event EventHandler? SessionExpired;

    private DateTimeOffset Now => _clock.GetUtcNow();

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
            _logger.LogInformation("Discarded expired persisted session for user {UserId}.", saved.UserId);
            await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        _store.Set(saved);
        _logger.LogInformation("Restored persisted session for user {UserId}.", saved.UserId);
        return saved;
    }

    public async Task SignInAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        _store.Set(session);
        await _persistence.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Signed in user {UserId}.", session.UserId);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var previousUser = _store.Current?.UserId;
        _store.Set(null);
        await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Signed out user {UserId}.", previousUser ?? "(none)");
    }

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
        current = _store.Current ?? current;
        var state = current.StateAt(Now, _refreshSkew);
        if (state is AuthSessionState.Active or AuthSessionState.Expiring)
        {
            // Active (someone else just refreshed) or still within the token's life:
            // keep using it until it actually expires.
            return current;
        }

        // Expired and unrecoverable: surface a graceful re-sign-in.
        _logger.LogWarning("Auth session expired and could not be refreshed for user {UserId}; signing out.", current.UserId);
        await SignOutAsync(cancellationToken).ConfigureAwait(false);
        SessionExpired?.Invoke(this, EventArgs.Empty);
        return null;
    }

    private async Task<AuthSession?> RefreshSingleFlightAsync(AuthSession seen, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check: another caller may have refreshed while we held in the queue.
            var current = _store.Current;
            if (current is not null && current.StateAt(Now, _refreshSkew) == AuthSessionState.Active)
            {
                return current;
            }

            var toRefresh = current ?? seen;
            if (_refresher is null || !toRefresh.CanRefresh)
            {
                return null;
            }

            AuthSession? refreshed;
            try
            {
                refreshed = await _refresher(toRefresh, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed refresh is not fatal on its own — the caller falls through
                // to the expiry decision (cancellation propagates intentionally).
                _logger.LogWarning(
                    ex,
                    "Session refresh threw {ExceptionType} for user {UserId}.",
                    ex.GetType().Name, toRefresh.UserId);
                refreshed = null;
            }

            if (refreshed is not null && !refreshed.IsExpired(Now))
            {
                await SignInAsync(refreshed, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Refreshed auth session for user {UserId}.", refreshed.UserId);
                return refreshed;
            }

            _logger.LogWarning("Session refresh returned no usable session for user {UserId}.", toRefresh.UserId);
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
