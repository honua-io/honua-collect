// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Automation.Http;

/// <summary>
/// Production <see cref="IHttpRequestTransport"/> backed by <see cref="HttpClient"/>.
/// Enforces <see cref="EndpointSecurity.IsTransportSecure"/> before sending so field
/// payloads and webhook secrets are never transmitted over cleartext HTTP to remote
/// hosts, attaches the <c>Idempotency-Key</c> header automatically, and converts
/// network/timeout exceptions into retryable <see cref="HttpSendResult.Offline"/>
/// outcomes so the <see cref="HttpRequestOutbox"/> applies its backoff policy.
/// </summary>
public sealed class HttpClientRequestTransport : IHttpRequestTransport
{
    /// <summary>Standard header carrying the outbox entry's idempotency key.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly HttpClient _httpClient;

    /// <summary>Creates a transport over the provided <see cref="HttpClient"/>.</summary>
    /// <param name="httpClient">The HTTP client used to dispatch requests.</param>
    public HttpClientRequestTransport(HttpClient httpClient)
        => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <inheritdoc />
    public async Task<HttpSendResult> SendAsync(HttpOutboxRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            return new HttpSendResult(400, $"Invalid request URL '{request.Url}'.");
        }

        if (!IsHttpScheme(uri) || !EndpointSecurity.IsTransportSecure(uri))
        {
            return new HttpSendResult(
                400,
                $"Insecure webhook/HTTP endpoint '{uri}': HTTPS is required for non-loopback hosts.");
        }

        HttpRequestMessage message;
        try
        {
            message = BuildRequestMessage(request, uri);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return new HttpSendResult(400, $"Invalid HTTP request configuration: {ex.Message}");
        }

        using (message)
        {
            try
            {
                using var response = await _httpClient.SendAsync(message, ct).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;
                return statusCode is >= 200 and < 300
                    ? HttpSendResult.Ok(statusCode)
                    : new HttpSendResult(statusCode, $"HTTP {statusCode} {response.ReasonPhrase}".Trim());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                return HttpSendResult.Offline($"Request timed out: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                return HttpSendResult.Offline(ex.Message);
            }
        }
    }

    private static bool IsHttpScheme(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private static HttpRequestMessage BuildRequestMessage(HttpOutboxRequest request, Uri uri)
    {
        var method = string.IsNullOrWhiteSpace(request.Method)
            ? HttpMethod.Post
            : new HttpMethod(request.Method.Trim().ToUpperInvariant());

        var message = new HttpRequestMessage(method, uri);

        string? contentType = null;
        foreach (var (rawName, value) in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var name = rawName.Trim();
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
                continue;
            }

            if (string.Equals(name, IdempotencyKeyHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            message.Headers.TryAddWithoutValidation(name, value);
        }

        message.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, request.IdempotencyKey);

        if (request.Body is not null)
        {
            var mediaType = ParseMediaType(contentType);
            message.Content = new StringContent(request.Body, Encoding.UTF8, mediaType);
        }

        return message;
    }

    private static string ParseMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "application/json";
        }

        return MediaTypeHeaderValue.TryParse(contentType, out var parsed) && !string.IsNullOrWhiteSpace(parsed.MediaType)
            ? parsed.MediaType
            : "application/json";
    }
}
