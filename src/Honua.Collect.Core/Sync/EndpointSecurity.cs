namespace Honua.Collect.Core.Sync;

/// <summary>
/// Transport-security policy for server endpoints: HTTPS is required, with
/// cleartext HTTP permitted only for local-development loopback hosts (including
/// the Android emulator's host alias <c>10.0.2.2</c>). Enforced at configuration
/// load so a misconfigured plaintext endpoint fails fast rather than leaking
/// credentials and field data over the network.
/// </summary>
public static class EndpointSecurity
{
    // 10.0.2.2 is the Android emulator's alias for the host machine's loopback.
    private static readonly string[] DevLoopbackHosts = ["10.0.2.2"];

    /// <summary>Whether the host is a development loopback (real loopback or the emulator alias).</summary>
    /// <param name="uri">The endpoint.</param>
    /// <returns><see langword="true"/> for loopback/emulator-host endpoints.</returns>
    public static bool IsDevelopmentLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback || DevLoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether the endpoint is acceptable: HTTPS, or cleartext only on a dev loopback.</summary>
    /// <param name="uri">The endpoint.</param>
    /// <returns><see langword="true"/> if the transport is allowed.</returns>
    public static bool IsTransportSecure(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || IsDevelopmentLoopback(uri);
    }

    /// <summary>Throws if the endpoint uses cleartext HTTP to a non-loopback host.</summary>
    /// <param name="uri">The endpoint to validate.</param>
    /// <exception cref="InvalidOperationException">The endpoint is insecure.</exception>
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
