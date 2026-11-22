// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public interface ISessionPersistence
{
    Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
