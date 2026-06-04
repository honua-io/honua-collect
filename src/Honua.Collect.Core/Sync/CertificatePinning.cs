using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// Optional public-key (SPKI) certificate pinning for the server transport. When
/// one or more pins are configured, the TLS handshake is accepted only if the
/// chain validates normally <b>and</b> the leaf certificate's
/// SubjectPublicKeyInfo SHA-256 matches a configured pin — defending against a
/// mis-issued or rogue-CA certificate that would otherwise chain to a trusted
/// root. Pinning the public key (not the whole certificate) means routine
/// certificate renewals that keep the same key do not break the app.
///
/// Pinning is <b>opt-in</b>: with no pins configured the factory returns
/// <see langword="null"/> and normal platform validation applies, so a
/// self-hosted deployment with its own certificate is never broken by a pin it
/// didn't set. The validation logic lives here (platform-neutral, unit-tested)
/// rather than in the MAUI app so it can be verified without a device.
/// </summary>
public static class CertificatePinning
{
    /// <summary>
    /// Computes the pin for a certificate: the Base64 of the SHA-256 of its
    /// SubjectPublicKeyInfo (the standard "SPKI pin" / RFC 7469 form, sans the
    /// <c>sha256/</c> prefix).
    /// </summary>
    /// <param name="certificate">The certificate to pin.</param>
    /// <returns>The Base64-encoded SPKI SHA-256 pin.</returns>
    public static string ComputeSpkiPin(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(SHA256.HashData(spki));
    }

    /// <summary>
    /// Decides whether a presented certificate is trusted under the configured pins.
    /// Requires an error-free chain in all cases; additionally requires a pin match
    /// when <paramref name="pins"/> is non-empty.
    /// </summary>
    /// <param name="leaf">The presented leaf certificate (null fails closed).</param>
    /// <param name="errors">Policy errors reported by the platform validator.</param>
    /// <param name="pins">Configured Base64 SPKI pins (empty = no pinning).</param>
    /// <returns><see langword="true"/> if the certificate should be trusted.</returns>
    public static bool IsTrusted(X509Certificate2? leaf, SslPolicyErrors errors, IReadOnlyCollection<string> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);

        // Never accept a chain the platform already rejected (expired, untrusted
        // root, name mismatch). Pinning is additive, never a downgrade.
        if (errors != SslPolicyErrors.None)
        {
            return false;
        }

        if (pins.Count == 0)
        {
            return true; // pinning disabled — platform validation already passed
        }

        if (leaf is null)
        {
            return false; // pinning on but no certificate to pin against
        }

        var presented = ComputeSpkiPin(leaf);
        foreach (var pin in pins)
        {
            // Constant-time-ish comparison via the framework's ordinal equality is
            // fine here: the pin set is a non-secret allowlist.
            if (string.Equals(pin?.Trim(), presented, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the server-certificate validation callback for an
    /// <see cref="HttpClientHandler"/>, or <see langword="null"/> when pinning is
    /// not configured (caller should then leave platform validation in place).
    /// </summary>
    /// <param name="pins">Configured Base64 SPKI pins.</param>
    /// <returns>A validation callback, or null when <paramref name="pins"/> is empty.</returns>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>?
        CreateValidationCallback(IReadOnlyCollection<string> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count == 0)
        {
            return null;
        }

        var pinned = pins.ToArray();
        return (_, certificate, _, errors) => IsTrusted(certificate, errors, pinned);
    }
}
