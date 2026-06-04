using System.Text.Json;
using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Presentation.Auth;

/// <summary>
/// Exchanges sign-in credentials for a short-lived bearer token at the server's
/// ArcGIS-style token endpoint (<c>/sharing/rest/generateToken</c>). On success it
/// returns an <see cref="AuthSession"/> whose <see cref="AuthSession.AccessToken"/>
/// is the server-issued token (NOT the user's password) and whose expiry is the
/// server's — so the transport never carries the raw password and stale tokens are
/// withheld. Extracted from the login page so the exchange is unit-testable.
/// </summary>
public sealed class ServerCredentialVerifier
{
    /// <summary>Default ArcGIS token endpoint path.</summary>
    public const string DefaultTokenEndpoint = "/sharing/rest/generateToken";

    private readonly HttpClient _http;
    private readonly string _tokenEndpoint;

    /// <summary>Creates the verifier.</summary>
    /// <param name="http">HTTP client pointed at the server (base address set).</param>
    /// <param name="tokenEndpoint">The token-issuing endpoint path.</param>
    public ServerCredentialVerifier(HttpClient http, string tokenEndpoint = DefaultTokenEndpoint)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        _tokenEndpoint = tokenEndpoint;
    }

    /// <summary>A <see cref="CredentialVerifier"/> over this verifier — pass to a <see cref="LoginViewModel"/>.</summary>
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

    /// <summary>
    /// Parses an ArcGIS <c>generateToken</c> response into a session, or null for an
    /// error/invalid-credentials body (which has no <c>token</c>). Testable without HTTP.
    /// </summary>
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
