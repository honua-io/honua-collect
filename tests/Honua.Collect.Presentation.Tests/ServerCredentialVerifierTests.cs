using System.Net;
using Honua.Collect.Presentation.Auth;

namespace Honua.Collect.Presentation.Tests;

public class ServerCredentialVerifierTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static (ServerCredentialVerifier, StubHandler) Build(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };
        return (new ServerCredentialVerifier(http), handler);
    }

    [Fact]
    public async Task Exchanges_credentials_for_a_bearer_token_at_generateToken()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
        var (verifier, handler) = Build(HttpStatusCode.OK, $$"""{"token":"portal-tok-abc","expires":{{expires}},"ssl":true}""");

        var session = await verifier.VerifyAsync("crew-1", "secret-key", CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal("crew-1", session!.UserId);
        Assert.Equal("portal-tok-abc", session.AccessToken); // the server token, NOT the password
        Assert.NotEqual("secret-key", session.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(expires), session.ExpiresAtUtc);
        // POSTed to the token endpoint with the credentials + IP client binding.
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.EndsWith("/sharing/rest/generateToken", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("username=crew-1", handler.LastBody);
        Assert.Contains("password=secret-key", handler.LastBody);
        Assert.Contains("client=ip", handler.LastBody);
    }

    [Fact]
    public async Task An_error_response_returns_null()
    {
        var (verifier, _) = Build(HttpStatusCode.OK, """{"error":{"code":400,"message":"Unable to generate token","details":["Invalid username or password."]}}""");
        Assert.Null(await verifier.VerifyAsync("crew-1", "wrong", CancellationToken.None));
    }

    [Fact]
    public async Task A_401_with_no_token_returns_null()
    {
        var (verifier, _) = Build(HttpStatusCode.Unauthorized, "Username or password is incorrect");
        Assert.Null(await verifier.VerifyAsync("crew-1", "wrong", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"expires\":123}")]            // no token
    [InlineData("{\"token\":\"\"}")]              // empty token
    public void ParseTokenResponse_returns_null_for_non_token_bodies(string body)
        => Assert.Null(ServerCredentialVerifier.ParseTokenResponse("u", body));

    [Fact]
    public void ParseTokenResponse_defaults_expiry_when_absent()
    {
        var session = ServerCredentialVerifier.ParseTokenResponse("u", """{"token":"t"}""");
        Assert.NotNull(session);
        Assert.True(session!.ExpiresAtUtc > DateTimeOffset.UtcNow);
    }
}
