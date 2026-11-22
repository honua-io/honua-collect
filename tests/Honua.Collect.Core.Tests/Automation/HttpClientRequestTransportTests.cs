// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Honua.Collect.Core.Automation.Http;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>Tests for <see cref="HttpClientRequestTransport"/> against a fake handler (no live network).</summary>
public class HttpClientRequestTransportTests
{
    private sealed class CapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return await responder(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Sends_https_request_with_idempotency_key_custom_headers_and_body()
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)));
        using var client = new HttpClient(handler);
        var transport = new HttpClientRequestTransport(client);

        var request = new HttpOutboxRequest
        {
            Method = "POST",
            Url = "https://hooks.example.test/catch/123",
            Body = """{"hello":"world"}""",
            IdempotencyKey = "wh:zapier:evt-1",
            Headers = new Dictionary<string, string>
            {
                ["X-Api-Key"] = "tok-123",
                ["Content-Type"] = "application/vnd.honua+json; charset=utf-8",
                ["Idempotency-Key"] = "should-be-overridden",
            },
        };

        var result = await transport.SendAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(202, result.StatusCode);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("wh:zapier:evt-1", Assert.Single(sent.Headers.GetValues("Idempotency-Key")));
        Assert.Equal("tok-123", Assert.Single(sent.Headers.GetValues("X-Api-Key")));
        Assert.Equal("application/vnd.honua+json", sent.Content?.Headers.ContentType?.MediaType);
        Assert.Equal("""{"hello":"world"}""", Assert.Single(handler.Bodies));
    }

    [Fact]
    public async Task Allows_cleartext_http_on_loopback_but_rejects_remote_cleartext_and_invalid_urls()
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var transport = new HttpClientRequestTransport(client);

        var loopback = await transport.SendAsync(new HttpOutboxRequest
        {
            Url = "http://127.0.0.1:8080/hook",
            IdempotencyKey = "k-loop",
        });
        Assert.True(loopback.IsSuccess);
        Assert.Single(handler.Requests);

        var insecure = await transport.SendAsync(new HttpOutboxRequest
        {
            Url = "http://hooks.example.test/insecure",
            IdempotencyKey = "k-bad",
        });
        Assert.False(insecure.IsSuccess);
        Assert.False(insecure.IsRetryable);
        Assert.Equal(400, insecure.StatusCode);
        Assert.Single(handler.Requests); // Never hit the network

        var invalid = await transport.SendAsync(new HttpOutboxRequest
        {
            Url = "not-a-uri",
            IdempotencyKey = "k-invalid",
        });
        Assert.False(invalid.IsSuccess);
        Assert.Equal(400, invalid.StatusCode);
        Assert.Single(handler.Requests);

        var fileUri = await transport.SendAsync(new HttpOutboxRequest
        {
            Url = "file:///etc/hosts",
            IdempotencyKey = "k-file",
        });
        Assert.False(fileUri.IsSuccess);
        Assert.False(fileUri.IsRetryable);
        Assert.Equal(400, fileUri.StatusCode);

        var ftpLoopback = await transport.SendAsync(new HttpOutboxRequest
        {
            Url = "ftp://127.0.0.1/data",
            IdempotencyKey = "k-ftp",
        });
        Assert.False(ftpLoopback.IsSuccess);
        Assert.False(ftpLoopback.IsRetryable);
        Assert.Equal(400, ftpLoopback.StatusCode);

        var badMethod = await transport.SendAsync(new HttpOutboxRequest
        {
            Method = "POST\r\nInjected",
            Url = "https://hooks.example.test/ok",
            IdempotencyKey = "k-method",
        });
        Assert.False(badMethod.IsSuccess);
        Assert.False(badMethod.IsRetryable);
        Assert.Equal(400, badMethod.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Maps_http_errors_network_exceptions_timeouts_and_caller_cancellation()
    {
        var handler = new CapturingHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/503" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                "/net" => throw new HttpRequestException("DNS failed"),
                "/timeout" => throw new TaskCanceledException("HttpClient timeout"),
                "/cancel" => Task.FromCanceled<HttpResponseMessage>(ct),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            };
        });

        using var client = new HttpClient(handler);
        var transport = new HttpClientRequestTransport(client);

        var s503 = await transport.SendAsync(new HttpOutboxRequest { Url = "https://example.test/503", IdempotencyKey = "k1" });
        Assert.Equal(503, s503.StatusCode);
        Assert.True(s503.IsRetryable);

        var net = await transport.SendAsync(new HttpOutboxRequest { Url = "https://example.test/net", IdempotencyKey = "k2" });
        Assert.Null(net.StatusCode);
        Assert.True(net.IsRetryable);

        var timeout = await transport.SendAsync(new HttpOutboxRequest { Url = "https://example.test/timeout", IdempotencyKey = "k3" });
        Assert.Null(timeout.StatusCode);
        Assert.True(timeout.IsRetryable);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(new HttpOutboxRequest { Url = "https://example.test/cancel", IdempotencyKey = "k4" }, cts.Token));
    }
}
