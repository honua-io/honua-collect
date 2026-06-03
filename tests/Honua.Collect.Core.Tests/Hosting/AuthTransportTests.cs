using System.Net;
using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Hosting;

public class AuthTransportTests
{
    private static AuthSession Session(string token) => new()
    {
        UserId = "u1",
        AccessToken = token,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
    };

    [Fact]
    public void Store_uses_fallback_until_signed_in_then_the_session_token()
    {
        var store = new AuthSessionStore("demo-key");
        Assert.Equal("demo-key", store.CurrentApiKey);
        Assert.Null(store.Current);

        store.Set(Session("real-token"));
        Assert.Equal("real-token", store.CurrentApiKey);
        Assert.Equal("u1", store.Current!.UserId);

        store.Set(null); // sign out
        Assert.Equal("demo-key", store.CurrentApiKey);
    }

    [Fact]
    public void Store_raises_changed_on_set()
    {
        var store = new AuthSessionStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Set(Session("t"));
        store.Set(null);

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
    public async Task Handler_injects_the_current_credential()
    {
        var store = new AuthSessionStore("demo-key");
        var sent = await SendThrough(store, new HttpRequestMessage(HttpMethod.Get, "https://x/y"));
        Assert.Equal("demo-key", sent.Headers.GetValues(AuthHeaderHandler.HeaderName).Single());

        store.Set(Session("real-token"));
        var sent2 = await SendThrough(store, new HttpRequestMessage(HttpMethod.Get, "https://x/y"));
        Assert.Equal("real-token", sent2.Headers.GetValues(AuthHeaderHandler.HeaderName).Single());
    }

    [Fact]
    public async Task Handler_does_not_override_an_explicit_header()
    {
        var store = new AuthSessionStore("demo-key");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://x/y");
        request.Headers.Add(AuthHeaderHandler.HeaderName, "explicit");

        var sent = await SendThrough(store, request);
        Assert.Equal("explicit", sent.Headers.GetValues(AuthHeaderHandler.HeaderName).Single());
    }

    [Fact]
    public async Task Handler_adds_no_header_when_no_credential()
    {
        var store = new AuthSessionStore(fallbackApiKey: null);
        var sent = await SendThrough(store, new HttpRequestMessage(HttpMethod.Get, "https://x/y"));
        Assert.False(sent.Headers.Contains(AuthHeaderHandler.HeaderName));
    }
}
