// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Sync;

public static class EndpointSecurity
{
    // 10.0.2.2 is the Android emulator's alias for the host machine's loopback.
    private static readonly string[] DevLoopbackHosts = ["10.0.2.2"];

    public static bool IsDevelopmentLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback || DevLoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsTransportSecure(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || IsDevelopmentLoopback(uri);
    }

    public static void EnsureSecureTransport(Uri uri)
    {
        if (!IsTransportSecure(uri))
        {
            throw new InvalidOperationException(
                $"Insecure server endpoint '{uri}': cleartext HTTP is only allowed for local-development " +
                "loopback hosts. Configure an https:// URL.");
        }
    }
}
