using System.Text.Json;
using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Presentation.Auth;

/// <summary>
/// Exchanges a held refresh token for a fresh, short-lived access token at the
/// server's token endpoint, producing a renewed <see cref="AuthSession"/>. This is
/// the concrete <see cref="SessionRefresher"/> the lifecycle calls when a session is
/// at/near expiry and a refresh token is available — the symmetric counterpart of
/// <see cref="ServerCredentialVerifier"/>, but driven by the refresh token rather
/// than the user's password, so re-authentication is silent and the password is
/// never re-sent.
/// </summary>
/// <remarks>
/// The wire shape follows the ArcGIS token endpoint: a successful response carries a
/// new <c>token</c> and <c>expires</c>, and — when the server supports rotation — a
/// new <c>refreshToken</c>. When the server returns no fresh refresh token, the
/// prior one is carried forward so the session can refresh again. A response with no
/// <c>token</c> (error / refused / revoked) yields <see langword="null"/>, which the
/// lifecycle treats as a refusal — failing closed to a re-sign-in rather than
/// silently dropping to anonymous.
///
/// NOTE: the server's current <c>generateToken</c> issues no refresh token, so in
/// production this refresher is dormant until that contract lands; it is wired and
/// fully unit-tested so activation is a one-line registration change.
/// </remarks>
public sealed class ServerTokenRefresher
{
    private readonly HttpClient _http;
    private readonly string _tokenEndpoint;

    /// <summary>Creates the refresher.</summary>
    /// <param name="http">HTTP client pointed at the server (base address set).</param>
    /// <param name="tokenEndpoint">The token-issuing endpoint path.</param>
    public ServerTokenRefresher(HttpClient http, string tokenEndpoint = ServerCredentialVerifier.DefaultTokenEndpoint)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        _tokenEndpoint = tokenEndpoint;
    }

    /// <summary>
    /// A <see cref="SessionRefresher"/> over this refresher — pass to an
    /// <see cref="AuthSessionManager"/>. Returns the renewed session, or
    /// <see langword="null"/> when the refresh is refused (forcing re-auth).
    /// </summary>
    /// <param name="expiring">The session at/near expiry to renew.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed session, or null when the server refuses the refresh.</returns>
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

    /// <summary>
    /// Parses a token-refresh response into a renewed session, preserving the
    /// caller's identity and scopes, or <see langword="null"/> for an error / refused
    /// body (which has no <c>token</c>). A new <c>refreshToken</c> is adopted when the
    /// server rotates it; otherwise the prior refresh token is carried forward so the
    /// session can refresh again. Testable without HTTP.
    /// </summary>
    /// <param name="expiring">The session being refreshed (identity/scopes preserved).</param>
    /// <param name="json">The server's token-endpoint response body.</param>
    /// <returns>The renewed session, or null when no token was issued.</returns>
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
