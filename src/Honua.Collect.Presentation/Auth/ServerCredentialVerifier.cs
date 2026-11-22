// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Presentation.Auth;

public sealed class ServerCredentialVerifier
{
    public const string DefaultTokenEndpoint = "/sharing/rest/generateToken";

    private readonly HttpClient _http;
    private readonly string _tokenEndpoint;

    public ServerCredentialVerifier(HttpClient http, string tokenEndpoint = DefaultTokenEndpoint)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        _tokenEndpoint = tokenEndpoint;
    }

    public async Task<AuthSession?> VerifyAsync(string username, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // IP-bound token avoids carrying a Referer on every later request.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["client"] = "ip",
            ["f"] = "json",
        });

        using var response = await _http.PostAsync(_tokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseTokenResponse(username, body);
    }

    public static AuthSession? ParseTokenResponse(string username, string json)
    {
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
                return null; // error / wrong credentials — no token issued
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var expiresUtc = root.TryGetProperty("expires", out var e) && e.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : DateTimeOffset.UtcNow.AddHours(1);

            return new AuthSession
            {
                UserId = username,
                DisplayName = username,
                AccessToken = token,
                ExpiresAtUtc = expiresUtc,
                Scopes = new HashSet<string>(StringComparer.Ordinal) { "collect.sync" },
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
