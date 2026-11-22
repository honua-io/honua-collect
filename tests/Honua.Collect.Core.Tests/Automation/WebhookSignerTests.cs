// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Automation.Webhooks;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>Tests for HMAC-SHA256 webhook payload signing and constant-time verification (#127).</summary>
public class WebhookSignerTests
{
    [Fact]
    public void Sign_and_verify_round_trip_succeeds()
    {
        const string secret = "test-key";
        const long ts = 1795200000;
        const string payload = """{"event":"record.created","record_id":"rec-1"}""";

        var sig = WebhookSigner.Sign(secret, ts, payload);

        Assert.StartsWith("sha256=", sig, StringComparison.Ordinal);
        Assert.Equal("sha256=".Length + 64, sig.Length);
        Assert.Equal(sig.ToLowerInvariant(), sig);
        Assert.True(WebhookSigner.Verify(secret, ts, payload, sig));
    }

    [Fact]
    public void Verify_rejects_wrong_secret_payload_timestamp_or_malformed_header()
    {
        const string secret = "whsec_secret_a";
        const long ts = 1795200000;
        const string payload = """{"record_id":"rec-1"}""";
        var sig = WebhookSigner.Sign(secret, ts, payload);

        Assert.False(WebhookSigner.Verify("whsec_secret_b", ts, payload, sig));
        Assert.False(WebhookSigner.Verify(secret, ts + 1, payload, sig));
        Assert.False(WebhookSigner.Verify(secret, ts, """{"record_id":"rec-2"}""", sig));
        Assert.False(WebhookSigner.Verify(secret, ts, payload, null));
        Assert.False(WebhookSigner.Verify(secret, ts, payload, string.Empty));
        Assert.False(WebhookSigner.Verify(secret, ts, payload, "sha1=deadbeef"));
        Assert.False(WebhookSigner.Verify(secret, ts, payload, "sha256=short"));
        Assert.False(WebhookSigner.Verify(string.Empty, ts, payload, sig));
    }

    [Fact]
    public void Sign_guards_null_or_empty_arguments()
    {
        Assert.Throws<ArgumentException>(() => WebhookSigner.Sign(string.Empty, 1, "{}"));
        Assert.Throws<ArgumentNullException>(() => WebhookSigner.Sign("secret", 1, null!));
    }
}
