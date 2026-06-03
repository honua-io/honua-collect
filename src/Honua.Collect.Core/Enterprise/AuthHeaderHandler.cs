namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// A <see cref="DelegatingHandler"/> that attaches the authenticated credential
/// from an <see cref="IAuthSessionStore"/> to every outbound request, so the
/// signed-in user's token — not a hardcoded key — is what reaches the server.
/// Registered on the shared HTTP client via <c>IHttpClientFactory</c>.
/// </summary>
public sealed class AuthHeaderHandler : DelegatingHandler
{
    /// <summary>The header used to present the credential.</summary>
    public const string HeaderName = "X-API-Key";

    private readonly IAuthSessionStore _sessions;

    /// <summary>Creates the handler over the session store.</summary>
    /// <param name="sessions">The source of the current credential.</param>
    public AuthHeaderHandler(IAuthSessionStore sessions)
        => _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credential = _sessions.CurrentApiKey;
        if (!string.IsNullOrEmpty(credential) && !request.Headers.Contains(HeaderName))
        {
            request.Headers.Add(HeaderName, credential);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
