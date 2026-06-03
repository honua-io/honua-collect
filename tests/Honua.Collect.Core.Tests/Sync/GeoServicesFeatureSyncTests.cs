using System.Net;
using System.Text.Json;
using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

public class GeoServicesFeatureSyncTests
{
    private sealed class CapturingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;
            CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private static FieldRecord Record()
    {
        var r = new FieldRecord { RecordId = "r1", FormId = "f", Location = new FieldGeoPoint(21.30, -157.82) };
        r.Values["site_name"] = "E2E Test Site";
        r.Values["priority"] = "high";
        r.Values["sync_version"] = 1;
        r.Values["empty"] = null; // omitted
        return r;
    }

    [Fact]
    public void BuildAddsJson_emits_point_geometry_and_typed_attributes()
    {
        using var doc = JsonDocument.Parse(GeoServicesFeatureSync.BuildAddsJson(Record()));
        var add = doc.RootElement[0];

        var geom = add.GetProperty("geometry");
        Assert.Equal(-157.82, geom.GetProperty("x").GetDouble()); // lon as x
        Assert.Equal(21.30, geom.GetProperty("y").GetDouble());   // lat as y
        Assert.Equal(4326, geom.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        var attrs = add.GetProperty("attributes");
        Assert.Equal("E2E Test Site", attrs.GetProperty("site_name").GetString());
        Assert.Equal(1, attrs.GetProperty("sync_version").GetInt32());        // numeric stays numeric
        Assert.False(attrs.TryGetProperty("empty", out _));                   // null omitted
    }

    [Fact]
    public async Task SubmitAsync_posts_applyEdits_and_parses_object_id()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"addResults":[{"objectId":6892003,"success":true}]}""");
        using var http = new HttpClient(handler);
        var sync = new GeoServicesFeatureSync(http);

        var result = await sync.SubmitAsync(Record(), new GeoServicesTarget("http://server:18080", "mobile_offline_demo", 68910));

        Assert.True(result.Success);
        Assert.Equal(6892003, result.ObjectId);
        Assert.EndsWith("/rest/services/mobile_offline_demo/FeatureServer/68910/applyEdits", handler.CapturedUri!.AbsoluteUri);
        Assert.Contains("adds=", handler.CapturedBody);
        Assert.Contains("f=json", handler.CapturedBody);
    }

    [Fact]
    public async Task SubmitAsync_surfaces_server_error()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"error":{"code":400,"message":"bad geometry"}}""");
        using var http = new HttpClient(handler);
        var sync = new GeoServicesFeatureSync(http);

        var result = await sync.SubmitAsync(Record(), new GeoServicesTarget("http://server", "s", 1));

        Assert.False(result.Success);
        Assert.Equal("bad geometry", result.Error);
    }

    [Fact]
    public async Task UpdateAsync_posts_updates_with_objectid_and_parses_updateResults()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"updateResults":[{"objectId":42,"success":true}]}""");
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).UpdateAsync(42, Record(), new GeoServicesTarget("http://s", "svc", 9));

        Assert.True(result.Success);
        Assert.Equal(42, result.ObjectId);
        Assert.Contains("updates=", handler.CapturedBody);
        using var doc = JsonDocument.Parse(System.Net.WebUtility.UrlDecode(
            handler.CapturedBody!.Split("updates=")[1].Split('&')[0]));
        Assert.Equal(42, doc.RootElement[0].GetProperty("attributes").GetProperty("objectid").GetInt64());
    }

    [Fact]
    public async Task DeleteAsync_posts_deletes_and_parses_deleteResults()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"deleteResults":[{"objectId":7,"success":true}]}""");
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).DeleteAsync(7, new GeoServicesTarget("http://s", "svc", 9));

        Assert.True(result.Success);
        Assert.Equal(7, result.ObjectId);
        Assert.Contains("deletes=%5B7%5D", handler.CapturedBody); // [7] url-encoded
    }

    [Fact]
    public async Task PerEdit_failure_surfaces_server_description()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"updateResults":[{"objectId":99,"success":false,"error":{"code":1000,"description":"Feature not found."}}]}""");
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FeatureSyncRetryPolicy.None)
            .UpdateAsync(99, Record(), new GeoServicesTarget("http://s", "svc", 9));

        Assert.False(result.Success);
        Assert.Equal("Feature not found.", result.Error);
    }

    private sealed class CapturingMultipartHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        public string? CapturedMediaType { get; private set; }
        public HttpMethod? CapturedMethod { get; private set; }
        public Uri? CapturedUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedMethod = request.Method;
            CapturedUri = request.RequestUri;
            CapturedMediaType = request.Content?.Headers.ContentType?.MediaType;
            CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private static string TempFileWithBytes()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        return path;
    }

    [Fact]
    public async Task AddAttachmentAsync_posts_multipart_to_addAttachment_and_parses_object_id()
    {
        var handler = new CapturingMultipartHandler(HttpStatusCode.OK, """{"addAttachmentResult":{"objectId":555,"success":true}}""");
        using var http = new HttpClient(handler);
        var sync = new GeoServicesFeatureSync(http);
        var file = TempFileWithBytes();
        try
        {
            var result = await sync.AddAttachmentAsync(42, file, "image/jpeg",
                new GeoServicesTarget("http://server:18080", "mobile_offline_demo", 68910));

            Assert.True(result.Success);
            Assert.Equal(555, result.ObjectId);
            Assert.Equal(HttpMethod.Post, handler.CapturedMethod);
            Assert.EndsWith("/rest/services/mobile_offline_demo/FeatureServer/68910/42/addAttachment", handler.CapturedUri!.AbsoluteUri);
            Assert.Equal("multipart/form-data", handler.CapturedMediaType);
            Assert.Contains("name=attachment", handler.CapturedBody!.Replace("\"", ""));
            Assert.Contains("name=f", handler.CapturedBody.Replace("\"", ""));
            Assert.Contains("json", handler.CapturedBody);
            Assert.Contains(Path.GetFileName(file), handler.CapturedBody);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddAttachmentAsync_defaults_content_type_to_octet_stream()
    {
        var handler = new CapturingMultipartHandler(HttpStatusCode.OK, """{"addAttachmentResult":{"objectId":1,"success":true}}""");
        using var http = new HttpClient(handler);
        var file = TempFileWithBytes();
        try
        {
            var result = await new GeoServicesFeatureSync(http)
                .AddAttachmentAsync(1, file, null, new GeoServicesTarget("http://s", "svc", 9));

            Assert.True(result.Success);
            Assert.Contains("application/octet-stream", handler.CapturedBody);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddAttachmentAsync_surfaces_server_error()
    {
        var handler = new CapturingMultipartHandler(HttpStatusCode.OK, """{"error":{"code":400,"message":"invalid attachment"}}""");
        using var http = new HttpClient(handler);
        var file = TempFileWithBytes();
        try
        {
            var result = await new GeoServicesFeatureSync(http)
                .AddAttachmentAsync(42, file, "image/jpeg", new GeoServicesTarget("http://s", "svc", 9));

            Assert.False(result.Success);
            Assert.Equal("invalid attachment", result.Error);
            Assert.Equal(400, result.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddAttachmentAsync_surfaces_failure_result_description()
    {
        var handler = new CapturingMultipartHandler(HttpStatusCode.OK,
            """{"addAttachmentResult":{"objectId":0,"success":false,"error":{"code":500,"description":"Could not store attachment."}}}""");
        using var http = new HttpClient(handler);
        var file = TempFileWithBytes();
        try
        {
            var result = await new GeoServicesFeatureSync(http)
                .AddAttachmentAsync(42, file, "image/jpeg", new GeoServicesTarget("http://s", "svc", 9));

            Assert.False(result.Success);
            Assert.Equal("Could not store attachment.", result.Error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddAttachmentAsync_validates_arguments()
    {
        using var http = new HttpClient(new CapturingMultipartHandler(HttpStatusCode.OK, "{}"));
        var sync = new GeoServicesFeatureSync(http);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sync.AddAttachmentAsync(1, "f", null, null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sync.AddAttachmentAsync(1, "  ", null, new GeoServicesTarget("http://s", "svc", 9)));
    }

    [Fact]
    public void AttachmentUrl_builds_expected_endpoint()
    {
        var target = new GeoServicesTarget("http://server:18080/", "svc", 9);
        Assert.Equal("http://server:18080/rest/services/svc/FeatureServer/9/42/addAttachment", target.AttachmentUrl(42));
    }

    private sealed class SequencedHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static readonly FeatureSyncRetryPolicy FastRetry = new(MaxAttempts: 4, BaseDelay: TimeSpan.Zero);
    private static readonly GeoServicesTarget T = new("http://s", "svc", 9);
    // Mirrors the real server: code 1000 is reused for transient "Update failed."
    // (retryable) and permanent "Feature not found." — so retryability is decided
    // from the message, not the code.
    private const string TransientFail = """{"updateResults":[{"objectId":9,"success":false,"error":{"code":1000,"description":"Update failed."}}]}""";
    private const string OkUpdate = """{"updateResults":[{"objectId":9,"success":true}]}""";

    [Fact]
    public async Task Retries_transient_failure_then_succeeds()
    {
        var handler = new SequencedHandler((HttpStatusCode.OK, TransientFail), (HttpStatusCode.OK, TransientFail), (HttpStatusCode.OK, OkUpdate));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).UpdateAsync(9, Record(), T);

        Assert.True(result.Success);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Retries_transient_http_5xx()
    {
        var handler = new SequencedHandler((HttpStatusCode.InternalServerError, "boom"), (HttpStatusCode.OK, OkUpdate));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).UpdateAsync(9, Record(), T);

        Assert.True(result.Success);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Does_not_retry_permanent_not_found()
    {
        var notFound = """{"updateResults":[{"objectId":9,"success":false,"error":{"code":1000,"description":"Feature not found."}}]}""";
        var handler = new SequencedHandler((HttpStatusCode.OK, notFound));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).UpdateAsync(9, Record(), T);

        Assert.False(result.Success);
        Assert.Equal(1, handler.Calls); // permanent failure not retried
    }

    [Fact]
    public async Task Does_not_retry_auth_failure_4xx()
    {
        var handler = new SequencedHandler((HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"unauthorized"}}"""));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).SubmitAsync(Record(), T);

        Assert.False(result.Success);
        Assert.Equal(1, handler.Calls); // 401 not retried
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_on_persistent_transient_failure()
    {
        var handler = new SequencedHandler((HttpStatusCode.OK, TransientFail));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).UpdateAsync(9, Record(), T);

        Assert.False(result.Success);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(4, handler.Calls);
    }

    private sealed class QueryHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public List<Uri> CapturedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = responses[Math.Min(CapturedUris.Count, responses.Length - 1)];
            CapturedUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public void QueryUrl_builds_expected_endpoint()
    {
        var target = new GeoServicesTarget("http://server:18080/", "svc", 9);
        Assert.Equal("http://server:18080/rest/services/svc/FeatureServer/9/query", target.QueryUrl);
    }

    [Fact]
    public async Task QueryAsync_hits_query_url_with_expected_params_and_decodes_features()
    {
        const string body = """
        {
          "objectIdFieldName": "objectid",
          "features": [
            { "attributes": { "objectid": 11, "site_name": "Alpha", "priority": "high", "count": 3 },
              "geometry": { "x": -157.82, "y": 21.30 } },
            { "attributes": { "objectid": 12, "site_name": "Bravo", "count": 7 },
              "geometry": { "x": -120.0, "y": 35.0 } }
          ]
        }
        """;
        var handler = new QueryHandler((HttpStatusCode.OK, body));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).QueryAsync(
            new GeoServicesTarget("http://server:18080", "mobile_offline_demo", 68910), "status = 'open'");

        Assert.True(result.Success);
        var uri = handler.CapturedUris[0].AbsoluteUri;
        Assert.Contains("/rest/services/mobile_offline_demo/FeatureServer/68910/query?", uri);
        Assert.Contains("f=json", uri);
        Assert.Contains("outFields=%2A", uri);            // *
        Assert.Contains("returnGeometry=true", uri);
        Assert.Contains("where=status%20%3D%20%27open%27", uri);

        Assert.Equal(2, result.Records.Count);
        var first = result.Records[0];
        Assert.Equal(11, first.ObjectId);
        Assert.Equal("11", first.Record.RecordId);
        Assert.Equal("Alpha", first.Record.Values["site_name"]);
        Assert.Equal(3L, Assert.IsType<long>(first.Record.Values["count"])); // numeric stays numeric
        Assert.False(first.Record.Values.ContainsKey("objectid"));      // object id is metadata, not a value
        Assert.Equal(21.30, first.Record.Location!.Latitude);           // y -> lat
        Assert.Equal(-157.82, first.Record.Location!.Longitude);        // x -> lon
        Assert.Equal(12, result.Records[1].ObjectId);
    }

    [Fact]
    public async Task QueryAsync_follows_paging_until_transfer_limit_clears()
    {
        const string page1 = """
        {
          "objectIdFieldName": "objectid",
          "exceededTransferLimit": true,
          "features": [ { "attributes": { "objectid": 1 } }, { "attributes": { "objectid": 2 } } ]
        }
        """;
        const string page2 = """
        {
          "objectIdFieldName": "objectid",
          "features": [ { "attributes": { "objectid": 3 } } ]
        }
        """;
        var handler = new QueryHandler((HttpStatusCode.OK, page1), (HttpStatusCode.OK, page2));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).QueryAsync(new GeoServicesTarget("http://s", "svc", 9));

        Assert.True(result.Success);
        Assert.Equal(2, handler.CapturedUris.Count);
        Assert.Contains("resultOffset=0", handler.CapturedUris[0].AbsoluteUri);
        Assert.Contains("resultOffset=2", handler.CapturedUris[1].AbsoluteUri); // advanced by first page count
        Assert.Equal([1L, 2L, 3L], result.Records.Select(r => r.ObjectId));
    }

    [Fact]
    public async Task QueryAsync_surfaces_server_error_without_throwing()
    {
        var handler = new QueryHandler((HttpStatusCode.OK, """{"error":{"code":400,"message":"Invalid where clause"}}"""));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).QueryAsync(new GeoServicesTarget("http://s", "svc", 9));

        Assert.False(result.Success);
        Assert.Equal("Invalid where clause", result.Error);
        Assert.Equal(400, result.ErrorCode);
        Assert.Empty(result.Records);
        Assert.Single(handler.CapturedUris); // no paging after an error
    }

    [Fact]
    public async Task QueryAsync_surfaces_http_failure()
    {
        var handler = new QueryHandler((HttpStatusCode.InternalServerError, "boom"));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).QueryAsync(new GeoServicesTarget("http://s", "svc", 9));

        Assert.False(result.Success);
        Assert.Equal(500, result.ErrorCode);
    }
}
