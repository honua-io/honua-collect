using Honua.Collect.Core.Licensing;

namespace Honua.Collect.App.Services;

/// <summary>
/// Persists the activated license key in the platform secure store (Android
/// Keystore / iOS Keychain). The token is itself signature-protected, so secure
/// storage is defense-in-depth rather than a confidentiality requirement; it keeps
/// the activated key off plain config and the record DB. Verification of the token
/// is always offline against the embedded authority key (<see cref="LicenseService"/>).
/// </summary>
public sealed class SecureStorageLicenseStore
{
    private const string KeyName = "collect_license_key";

    /// <summary>Loads the activated license token, or null when none is stored.</summary>
    public Task<string?> LoadAsync() => SecureStorage.Default.GetAsync(KeyName);

    /// <summary>Stores an activated license token.</summary>
    /// <param name="token">The signed license key.</param>
    public Task SaveAsync(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return SecureStorage.Default.SetAsync(KeyName, token);
    }

    /// <summary>Removes the activated license token (deactivate).</summary>
    public void Clear() => SecureStorage.Default.Remove(KeyName);
}
