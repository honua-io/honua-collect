// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.App.Services;

public sealed class SecureStorageSessionPersistence : ISessionPersistence
{
    private const string KeyName = "collect_auth_session";

    public async Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await SecureStorage.Default.GetAsync(KeyName).ConfigureAwait(false);
        return AuthSessionSerializer.TryDeserialize(json);
    }

    public Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SecureStorage.Default.SetAsync(KeyName, AuthSessionSerializer.Serialize(session));
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        SecureStorage.Default.Remove(KeyName);
        return Task.CompletedTask;
    }
}
