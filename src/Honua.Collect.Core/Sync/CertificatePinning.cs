// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Honua.Collect.Core.Sync;

public static class CertificatePinning
{
    public static string ComputeSpkiPin(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(SHA256.HashData(spki));
    }

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
