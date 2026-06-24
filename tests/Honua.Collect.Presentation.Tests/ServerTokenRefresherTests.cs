using System.Net;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Auth;

namespace Honua.Collect.Presentation.Tests;

public class ServerTokenRefresherTests
{
    private static AuthSession Expiring(string? refresh = "rt-1", string token = "old-token") => new()
    {
        UserId = "crew-1",
        DisplayName = "Crew One",
        AccessToken = token,
        RefreshToken = refresh,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
        Scopes = new HashSet<string>(StringComparer.Ordinal) { "collect.sync" },
    };

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

    private static (ServerTokenRefresher, StubHandler) Build(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };
        return (new ServerTokenRefresher(http), handler);
    }

    // --- refresh-before-expiry (happy path over the wire) --------------------

    [Fact]
    public async Task Exchanges_the_refresh_token_for_a_fresh_access_token()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
        var (refresher, handler) = Build(
            HttpStatusCode.OK,
            $$"""{"token":"fresh-tok","refreshToken":"rt-2","expires":{{expires}}}""");

        var renewed = await refresher.RefreshAsync(Expiring(), CancellationToken.None);

        Assert.NotNull(renewed);
        Assert.Equal("fresh-tok", renewed!.AccessToken);   // new access token
        Assert.Equal("rt-2", renewed.RefreshToken);        // rotated refresh token adopted
        Assert.Equal("crew-1", renewed.UserId);            // identity preserved
        Assert.Contains("collect.sync", renewed.Scopes);   // scopes preserved
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(expires), renewed.ExpiresAtUtc);

        // Posts the refresh-token grant to the token endpoint — never the password.
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.EndsWith("/sharing/rest/generateToken", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("grant_type=refresh_token", handler.LastBody);
        Assert.Contains("refresh_token=rt-1", handler.LastBody);
    }

    [Fact]
    public async Task Carries_forward_the_prior_refresh_token_when_the_server_does_not_rotate_it()
    {
        var (refresher, _) = Build(HttpStatusCode.OK, """{"token":"fresh-tok"}""");

        var renewed = await refresher.RefreshAsync(Expiring(refresh: "rt-keep"), CancellationToken.None);

        Assert.NotNull(renewed);
        Assert.Equal("fresh-tok", renewed!.AccessToken);
        Assert.Equal("rt-keep", renewed.RefreshToken); // can refresh again next time
    }

    [Fact]
    public async Task No_refresh_token_held_returns_null_without_calling_the_server()
    {
        var (refresher, handler) = Build(HttpStatusCode.OK, """{"token":"fresh-tok"}""");

        var renewed = await refresher.RefreshAsync(Expiring(refresh: null), CancellationToken.None);

        Assert.Null(renewed);
        Assert.Null(handler.Last); // never hit the wire
    }

    // --- refresh-failure handling (refused / error → null → fail closed) ------

    [Fact]
    public async Task A_refused_refresh_returns_null()
    {
        var (refresher, _) = Build(
            HttpStatusCode.OK,
            """{"error":{"code":498,"message":"Invalid token","details":["refresh token expired"]}}""");

        Assert.Null(await refresher.RefreshAsync(Expiring(), CancellationToken.None));
    }

    [Fact]
    public async Task A_401_with_no_token_returns_null()
    {
        var (refresher, _) = Build(HttpStatusCode.Unauthorized, "token revoked");
        Assert.Null(await refresher.RefreshAsync(Expiring(), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"expires\":123}")]   // no token
    [InlineData("{\"token\":\"\"}")]     // empty token
    public void ParseRefreshResponse_returns_null_for_non_token_bodies(string body)
        => Assert.Null(ServerTokenRefresher.ParseRefreshResponse(Expiring(), body));

    [Fact]
    public void ParseRefreshResponse_defaults_expiry_when_absent()
    {
        var renewed = ServerTokenRefresher.ParseRefreshResponse(Expiring(), """{"token":"t"}""");
        Assert.NotNull(renewed);
        Assert.True(renewed!.ExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Refresh_null_session_throws()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => Build(HttpStatusCode.OK, "{}").Item1.RefreshAsync(null!, CancellationToken.None));
}
