// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

/// <summary>
/// Covers <see cref="GeoServicesFeatureSync.QueryPageAsync"/>: single-page offset/count
/// parameters, continuation cursor advancement, and error surfacing for resumable pulls.
/// </summary>
public sealed class GeoServicesFeatureSyncPageTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return Task.FromResult(responder(request));
        }
    }

    private static GeoServicesTarget Target => new("inspections", 0);

    [Fact]
    public async Task QueryPageAsync_sends_offset_and_count_and_advances_cursor()
    {
        const string pageJson = """
            {
              "objectIdFieldName": "objectid",
              "exceededTransferLimit": true,
              "features": [
                { "attributes": { "objectid": 10, "status": "open" } },
                { "attributes": { "objectid": 11, "status": "closed" } }
              ]
            }
            """;

        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(pageJson, Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://honua.test/") };
        var sync = new GeoServicesFeatureSync(http);

        var page = await sync.QueryPageAsync(Target, offset: 20, pageSize: 2);

        Assert.True(page.HasMore);
        Assert.Equal(22, page.NextOffset);
        Assert.Equal(2, page.Records.Count);
        Assert.Equal(10, page.Records[0].ObjectId);
        var uri = Assert.Single(handler.Requests)!.ToString();
        Assert.Contains("resultOffset=20", uri);
        Assert.Contains("resultRecordCount=2", uri);
        Assert.Contains("orderByFields=objectid", uri);
    }

    [Fact]
    public async Task QueryPageAsync_throws_on_http_and_service_errors()
    {
        using var httpErrHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("bad gateway"),
        });
        using var httpErr = new HttpClient(httpErrHandler) { BaseAddress = new Uri("https://honua.test/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GeoServicesFeatureSync(httpErr).QueryPageAsync(Target, offset: 0, pageSize: 10));

        const string serviceErrorJson = """{"error":{"code":400,"message":"bad filter"}}""";
        using var svcErrHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(serviceErrorJson, Encoding.UTF8, "application/json"),
        });
        using var svcErr = new HttpClient(svcErrHandler) { BaseAddress = new Uri("https://honua.test/") };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GeoServicesFeatureSync(svcErr).QueryPageAsync(Target, offset: 0, pageSize: 10));
        Assert.Contains("bad filter", ex.Message);
    }
}
