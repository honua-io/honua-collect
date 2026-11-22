// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Presentation.Auth;

public sealed class ServerTokenRefresher
{
    private readonly HttpClient _http;
    private readonly string _tokenEndpoint;

    public ServerTokenRefresher(HttpClient http, string tokenEndpoint = ServerCredentialVerifier.DefaultTokenEndpoint)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        _tokenEndpoint = tokenEndpoint;
    }

    public async Task<AuthSession?> RefreshAsync(AuthSession expiring, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expiring);
        if (string.IsNullOrEmpty(expiring.RefreshToken))
        {
            // No refresh token to exchange — nothing this can do; the lifecycle
            // would not have called us, but guard anyway.
            return null;
        }

        // OAuth-style refresh-token grant. No username/password — the refresh token
        // is the credential, so the password is never re-transmitted.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = expiring.RefreshToken,
            ["client"] = "ip",
            ["f"] = "json",
        });

        using var response = await _http.PostAsync(_tokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseRefreshResponse(expiring, body);
    }

    public static AuthSession? ParseRefreshResponse(AuthSession expiring, string json)
    {
        ArgumentNullException.ThrowIfNull(expiring);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String)
            {
                return null; // error / refused / revoked — no fresh token issued
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var expiresUtc = root.TryGetProperty("expires", out var e) && e.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : DateTimeOffset.UtcNow.AddHours(1);

            // Adopt a rotated refresh token when present; otherwise keep the existing
            // one so the next refresh still has a credential to present.
            var refreshToken = root.TryGetProperty("refreshToken", out var rt)
                && rt.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(rt.GetString())
                    ? rt.GetString()
                    : expiring.RefreshToken;

            return expiring with
            {
                AccessToken = token,
                RefreshToken = refreshToken,
                ExpiresAtUtc = expiresUtc,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
