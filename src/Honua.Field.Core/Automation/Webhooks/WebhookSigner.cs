// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;

namespace Honua.Field.Core.Automation.Webhooks;

/// <summary>Standard HTTP header names attached to Honua Field webhook deliveries (#127).</summary>
public static class WebhookHeaders
{
    /// <summary>Canonical event name (e.g. <c>record.created</c>).</summary>
    public const string Event = "X-Honua-Event";

    /// <summary>Stable event/delivery identifier.</summary>
    public const string Delivery = "X-Honua-Delivery";

    /// <summary>Unix timestamp (seconds, UTC) used in signature computation.</summary>
    public const string Timestamp = "X-Honua-Timestamp";

    /// <summary>HMAC-SHA256 signature header (<c>sha256=&lt;lowercase-hex&gt;</c>).</summary>
    public const string Signature256 = "X-Honua-Signature-256";
}

/// <summary>
/// Pure-managed HMAC-SHA256 webhook payload signer and constant-time verifier (#127).
/// Signs <c>&lt;unixTimestampSeconds&gt;.&lt;rawPayload&gt;</c> so receivers can reject both
/// tampered bodies and replayed requests outside their tolerance window.
/// </summary>
public static class WebhookSigner
{
    private const string Prefix = "sha256=";

    /// <summary>Computes the <see cref="WebhookHeaders.Signature256"/> header value.</summary>
    /// <param name="secret">Shared signing secret.</param>
    /// <param name="unixTimestampSeconds">UTC Unix timestamp in seconds.</param>
    /// <param name="payload">Raw UTF-8 JSON payload string.</param>
    /// <returns>The formatted signature header (<c>sha256=&lt;hex&gt;</c>).</returns>
    public static string Sign(string secret, long unixTimestampSeconds, string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(payload);

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes($"{unixTimestampSeconds}.{payload}");
        var mac = HMACSHA256.HashData(keyBytes, messageBytes);
        return Prefix + Convert.ToHexStringLower(mac);
    }

    /// <summary>
    /// Verifies a <see cref="WebhookHeaders.Signature256"/> header in constant time.
    /// </summary>
    /// <param name="secret">Shared signing secret.</param>
    /// <param name="unixTimestampSeconds">UTC Unix timestamp from <see cref="WebhookHeaders.Timestamp"/>.</param>
    /// <param name="payload">Raw UTF-8 JSON payload received.</param>
    /// <param name="signatureHeader">The signature header value to validate.</param>
    /// <returns><see langword="true"/> when the signature matches; otherwise <see langword="false"/>.</returns>
    public static bool Verify(
        string secret,
        long unixTimestampSeconds,
        string payload,
        string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret)
            || payload is null
            || string.IsNullOrEmpty(signatureHeader)
            || !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Sign(secret, unixTimestampSeconds, payload);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(signatureHeader);

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
