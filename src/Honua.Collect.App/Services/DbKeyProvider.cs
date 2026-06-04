using System.Security.Cryptography;

namespace Honua.Collect.App.Services;

/// <summary>
/// Provides the at-rest encryption key for the local record database. The key is
/// a 256-bit random value generated on first run and held in the platform secure
/// store (Android Keystore / iOS Keychain) — never in the database, app config, or
/// plaintext storage — so the SQLCipher-encrypted DB can only be opened on this
/// device by this app.
/// </summary>
public static class DbKeyProvider
{
    private const string KeyName = "collect_db_key";

    /// <summary>Returns the device DB key, generating and storing one on first use.</summary>
    /// <returns>A base64-encoded 256-bit key.</returns>
    public static async Task<string> GetOrCreateKeyAsync()
    {
        var existing = await SecureStorage.Default.GetAsync(KeyName);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await SecureStorage.Default.SetAsync(KeyName, key);
        return key;
    }
}
