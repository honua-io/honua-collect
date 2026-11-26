// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace Honua.Field.Core.Storage;

/// <summary>Shares one secure-storage key across the device's encrypted SQLite databases.</summary>
public sealed class DeviceDatabaseKey(Func<Task<string?>> readKey, Func<string, Task> storeKey, params string[] databasePaths)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Returns the installed key, or creates one on a fresh installation.</summary>
    public async Task<string> GetOrCreateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await readKey().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }

            foreach (var path in databasePaths)
            {
                if (Exists(path) || Exists(path + "-wal") || Exists(path + "-journal") || Exists(path + "-shm"))
                {
                    throw new InvalidOperationException(
                        "The device key for existing field data is unavailable. Unlock the device and retry. " +
                        "If the problem persists, keep the app installed and contact your administrator.");
                }
            }

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await storeKey(key).ConfigureAwait(false);
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
