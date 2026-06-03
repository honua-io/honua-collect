using System.Net;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Auth;

namespace Honua.Collect.Presentation.Tests;

public class ServerCredentialVerifierTests
{
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static (ServerCredentialVerifier, StubHandler) Build(HttpStatusCode status)
    {
        var handler = new StubHandler(status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };
        return (new ServerCredentialVerifier(http, "/rest/services/svc/FeatureServer/1?f=json"), handler);
    }

    [Fact]
    public async Task Valid_credentials_yield_a_session_carrying_the_token()
    {
        var (verifier, handler) = Build(HttpStatusCode.OK);

        var session = await verifier.VerifyAsync("crew-1", "secret-key", CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal("crew-1", session!.UserId);
        Assert.Equal("secret-key", session.AccessToken);
        // The probe presents the entered credential explicitly.
        Assert.Equal("secret-key", handler.Last!.Headers.GetValues(AuthHeaderHandler.HeaderName).Single());
        Assert.EndsWith("/rest/services/svc/FeatureServer/1?f=json", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Rejected_credentials_return_null()
    {
        var (verifier, _) = Build(HttpStatusCode.Unauthorized);
        Assert.Null(await verifier.VerifyAsync("crew-1", "wrong", CancellationToken.None));
    }

    [Fact]
    public async Task Session_expiry_is_derived_from_the_supplied_clock()
    {
        var (verifier, _) = Build(HttpStatusCode.OK);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var session = await verifier.VerifyAtAsync("u", "k", now, CancellationToken.None);

        Assert.Equal(now.AddHours(8), session!.ExpiresAtUtc);
    }
}
