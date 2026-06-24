using System.Net;
using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Hosting;

public class AuthTransportTests
{
    private static AuthSession Session(string token, TimeSpan? validFor = null) => new()
    {
        UserId = "u1",
        AccessToken = token,
        ExpiresAtUtc = DateTimeOffset.UtcNow + (validFor ?? TimeSpan.FromHours(1)),
    };

    [Fact]
    public void Store_exposes_session_and_fallback_independently()
    {
        var store = new AuthSessionStore("demo-key");
        Assert.Null(store.Current);
        Assert.Equal("demo-key", store.FallbackApiKey);

        var raised = 0;
        store.Changed += (_, _) => raised++;
        store.Set(Session("t"));
        Assert.Equal("u1", store.Current!.UserId);
        store.Set(null);
        Assert.Null(store.Current);
        Assert.Equal(2, raised);
    }

    private sealed class CapturingInner : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<HttpRequestMessage> SendThrough(IAuthSessionStore store, HttpRequestMessage request)
    {
        var inner = new CapturingInner();
        var handler = new AuthHeaderHandler(store) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        await invoker.SendAsync(request, CancellationToken.None);
        return inner.Last!;
    }

    [Fact]
    public async Task Live_session_presents_a_bearer_token_not_an_api_key()
    {
        var store = new AuthSessionStore("demo-key");
        store.Set(Session("portal-token-123"));

        var sent = await SendThrough(store, new HttpRequestMessage(HttpMethod.Get, "https://x/y"));

        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        Assert.Equal("portal-token-123", sent.Headers.Authorization!.Parameter);
        Assert.False(sent.Headers.Contains(AuthHeaderHandler.ApiKeyHeader));
    }

    [Fact]
    public async Task Signed_out_falls_back_to_the_api_key()
    {
        var store = new AuthSessionStore("demo-key");

        var sent = await SendThrough(store, new HttpRequestMessage(HttpMethod.Get, "https://x/y"));

        Assert.Null(sent.Headers.Authorization);
        Assert.Equal("demo-key", sent.Headers.GetValues(AuthHeaderHandler.ApiKeyHeader).Single());
    }

    [Fact]
    public async Task Expired_session_fails_closed_no_token_and_no_api_key_fallback()
    {
        // A session WAS established and then lapsed: fail closed. The request goes out
        // with no credential (surfacing a 401 / re-sign-in) rather than silently
        // downgrading to the anonymous fallback key.
        var store = new AuthSessionStore("demo-key");
        store.Set(Session("stale", validFor: TimeSpan.FromMinutes(-1))); // expired

        var sent = await SendThrough(store, new HttpRequestMessage(HttpMethod.Get, "https://x/y"));

        Assert.Null(sent.Headers.Authorization);
        Assert.False(sent.Headers.Contains(AuthHeaderHandler.ApiKeyHeader));
    }

    [Fact]
    public async Task Validator_refreshes_a_near_expiry_session_before_presenting_it()
    {
        var store = new AuthSessionStore("demo-key");
        store.Set(Session("near-expiry", validFor: TimeSpan.FromSeconds(30)));
        var refreshed = Session("fresh-token");

        async Task<AuthSession?> Validate(CancellationToken _)
        {
            store.Set(refreshed); // simulate EnsureValidAsync swapping in a refreshed session
            return await Task.FromResult(refreshed);
        }

        var inner = new CapturingInner();
        var handler = new AuthHeaderHandler(store, Validate) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://x/y"), CancellationToken.None);

        Assert.Equal("Bearer", inner.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("fresh-token", inner.Last!.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Validator_returning_null_fails_closed_no_fallback_after_a_session_existed()
    {
        // EnsureValidAsync expired the session and returned null: present nothing.
        var store = new AuthSessionStore("demo-key");
        store.Set(Session("stale", validFor: TimeSpan.FromMinutes(-1)));

        Task<AuthSession?> Validate(CancellationToken _)
        {
            store.Set(null); // manager signs out on unrecoverable expiry
            return Task.FromResult<AuthSession?>(null);
        }

        var inner = new CapturingInner();
        var handler = new AuthHeaderHandler(store, Validate) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://x/y"), CancellationToken.None);

        Assert.Null(inner.Last!.Headers.Authorization);
        Assert.False(inner.Last!.Headers.Contains(AuthHeaderHandler.ApiKeyHeader));
    }

    [Fact]
    public async Task No_session_and_no_fallback_adds_no_auth()
    {
        var store = new AuthSessionStore(fallbackApiKey: null);

        var sent = await SendThrough(store, new HttpRequestMessage(HttpMethod.Get, "https://x/y"));

        Assert.Null(sent.Headers.Authorization);
        Assert.False(sent.Headers.Contains(AuthHeaderHandler.ApiKeyHeader));
    }

    [Fact]
    public async Task An_explicit_authorization_header_is_not_overridden()
    {
        var store = new AuthSessionStore("demo-key");
        store.Set(Session("portal-token-123"));

        var request = new HttpRequestMessage(HttpMethod.Get, "https://x/y");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "explicit");

        var sent = await SendThrough(store, request);
        Assert.Equal("explicit", sent.Headers.Authorization!.Parameter);
    }
}
