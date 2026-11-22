// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace Honua.Collect.App.Services;

public static class DbKeyProvider
{
    private const string KeyName = "collect_db_key";

    // Serializes the get-or-create so two first-launch callers (e.g. the store probe
    // and the first screen) cannot each generate a key and have the second SetAsync
    // overwrite the first — which would orphan the database the first key encrypted
    // and force a silent data-loss reset of unsynced records (AUD-295).
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<string> GetOrCreateKeyAsync()
    {
        var existing = await SecureStorage.Default.GetAsync(KeyName).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-check inside the lock: a concurrent caller may have created and stored
            // the key while we waited, in which case we must return that one — never
            // generate and overwrite it.
            existing = await SecureStorage.Default.GetAsync(KeyName).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await SecureStorage.Default.SetAsync(KeyName, key).ConfigureAwait(false);
            return key;
        }
        finally
        {
            Gate.Release();
        }
    }
}
