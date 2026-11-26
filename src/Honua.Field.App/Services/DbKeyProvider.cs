// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Storage;

namespace Honua.Field.App.Services;

internal static class DbKeyProvider
{
    private const string KeyName = "collect_db_key";

    internal static DeviceDatabaseKey Create(params string[] databasePaths) => new(
        () => SecureStorage.Default.GetAsync(KeyName),
        value => SecureStorage.Default.SetAsync(KeyName, value),
        databasePaths);
}
