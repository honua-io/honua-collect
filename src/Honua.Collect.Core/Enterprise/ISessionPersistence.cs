namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Persists the signed-in <see cref="AuthSession"/> across app restarts so a user
/// isn't forced to re-enter credentials every launch. Implementations MUST store
/// the session only in platform secure storage (Android Keystore / iOS Keychain) —
/// never the record database or plain config — and hold it encrypted at rest.
/// Expiry is enforced on load by <see cref="AuthSessionManager"/>, so a stale token
/// is dropped rather than resumed.
/// </summary>
public interface ISessionPersistence
{
    /// <summary>Loads the persisted session, or null when none is stored / unreadable.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored session, or null.</returns>
    Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the session to secure storage, replacing any previous one.</summary>
    /// <param name="session">The session to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default);

    /// <summary>Removes any persisted session (sign-out / expiry).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
