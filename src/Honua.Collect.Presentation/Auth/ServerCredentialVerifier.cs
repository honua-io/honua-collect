using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Presentation.Auth;

/// <summary>
/// Verifies sign-in credentials against the Honua server by issuing an
/// authenticated probe request: a 2xx response yields an <see cref="AuthSession"/>,
/// anything else is treated as invalid credentials. Extracted from the login page
/// so the network logic is unit-testable (the page only wires it to the
/// <see cref="LoginViewModel"/>).
/// </summary>
public sealed class ServerCredentialVerifier
{
    private readonly HttpClient _http;
    private readonly string _probePath;
    private readonly TimeSpan _sessionLifetime;

    /// <summary>Creates the verifier.</summary>
    /// <param name="http">HTTP client pointed at the server (base address set).</param>
    /// <param name="probePath">A relative path that requires authentication.</param>
    /// <param name="sessionLifetime">How long an issued session is valid; defaults to 8 hours.</param>
    public ServerCredentialVerifier(HttpClient http, string probePath, TimeSpan? sessionLifetime = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(probePath);
        _probePath = probePath;
        _sessionLifetime = sessionLifetime ?? TimeSpan.FromHours(8);
    }

    /// <summary>
    /// A <see cref="CredentialVerifier"/> over this verifier — pass to a
    /// <see cref="LoginViewModel"/>.
    /// </summary>
    public Task<AuthSession?> VerifyAsync(string username, string password, CancellationToken cancellationToken)
    {
        return VerifyAtAsync(username, password, DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>Verify with an explicit clock, so session expiry is deterministic in tests.</summary>
    public async Task<AuthSession?> VerifyAtAsync(
        string username, string password, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        using var request = new HttpRequestMessage(HttpMethod.Get, _probePath);
        request.Headers.Add(AuthHeaderHandler.HeaderName, password); // test the user's own credential
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return new AuthSession
        {
            UserId = username,
            DisplayName = username,
            AccessToken = password,
            ExpiresAtUtc = nowUtc + _sessionLifetime,
            Scopes = new HashSet<string>(StringComparer.Ordinal) { "collect.sync" },
        };
    }
}
