using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.App.Services;

/// <summary>
/// Persists the auth session in the platform secure store (Android Keystore / iOS
/// Keychain) only — never the record DB or plain config — so a session can resume
/// across restarts without leaving a readable token at rest. This keeps the
/// security review's "in-memory / encrypted-at-rest" posture: the secure store is
/// hardware-backed and per-app. Expiry is enforced on load by
/// <see cref="AuthSessionManager"/>, which discards a stale token rather than
/// presenting it.
/// </summary>
public sealed class SecureStorageSessionPersistence : ISessionPersistence
{
    private const string KeyName = "collect_auth_session";

    /// <inheritdoc />
    public async Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await SecureStorage.Default.GetAsync(KeyName).ConfigureAwait(false);
        return AuthSessionSerializer.TryDeserialize(json);
    }

    /// <inheritdoc />
    public Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SecureStorage.Default.SetAsync(KeyName, AuthSessionSerializer.Serialize(session));
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        SecureStorage.Default.Remove(KeyName);
        return Task.CompletedTask;
    }
}
