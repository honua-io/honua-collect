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

    // ---- Non-idempotent add retry safety (duplicate prevention) --------------

    private const string OkAdd = """{"addResults":[{"objectId":5,"success":true}]}""";

    [Fact]
    public async Task SubmitAsync_does_not_retry_an_add_after_a_transport_failure()
    {
        // A transport failure leaves no server response: the add may already have
        // committed. Auto-retrying would risk a duplicate feature, so the add must
        // fail fast (one attempt) rather than re-POST. (Contrast: UpdateAsync, an
        // idempotent edit, DOES retry the same transport failure.)
        var handler = new FlakyHandler(new HttpRequestException("connection reset"), (HttpStatusCode.OK, OkAdd));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).SubmitAsync(Record(), T);

        Assert.False(result.Success);
        Assert.Equal(1, handler.Calls); // never retried -> cannot duplicate
        Assert.Contains("connection reset", result.Error);
    }

    [Fact]
    public async Task SubmitAsync_does_not_retry_an_add_on_an_ambiguous_http_5xx()
    {
        // A 5xx may be a 502-after-commit, so the add is ambiguous and must not be
        // auto-retried (which could duplicate the feature).
        var handler = new SequencedHandler((HttpStatusCode.InternalServerError, "boom"), (HttpStatusCode.OK, OkAdd));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).SubmitAsync(Record(), T);

        Assert.False(result.Success);
        Assert.Equal(500, result.ErrorCode);
        Assert.Equal(1, handler.Calls); // ambiguous 5xx not retried for a non-idempotent add
    }

    [Fact]
    public async Task SubmitAsync_retries_an_add_on_a_server_acknowledged_transient_failure()
    {
        // An HTTP 200 with success:false under rollbackOnFailure is provably
        // non-committed, so retrying the add is safe and is still done.
        const string transientAddFail = """{"addResults":[{"success":false,"error":{"code":1000,"description":"Add failed."}}]}""";
        var handler = new SequencedHandler((HttpStatusCode.OK, transientAddFail), (HttpStatusCode.OK, OkAdd));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).SubmitAsync(Record(), T);

        Assert.True(result.Success);
        Assert.Equal(5, result.ObjectId);
        Assert.Equal(2, handler.Calls); // safe transient retry still happens
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
    public async Task QueryAsync_pages_on_raw_feature_count_when_some_features_lack_object_ids()
    {
        // Page 1 returns 3 raw features but one (the middle) has no recognized
        // object id, so it's dropped on decode. Paging must still advance by the
        // RAW count (3), not the decoded count (2) — otherwise the next offset is
        // wrong and boundary features get re-fetched.
        const string page1 = """
        {
          "objectIdFieldName": "objectid",
          "exceededTransferLimit": true,
          "features": [
            { "attributes": { "objectid": 1 } },
            { "attributes": { "site_name": "no oid" } },
            { "attributes": { "objectid": 2 } }
          ]
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
        Assert.Contains("resultOffset=3", handler.CapturedUris[1].AbsoluteUri); // raw count, not decoded count (2)
        Assert.Equal([1L, 2L, 3L], result.Records.Select(r => r.ObjectId));
    }

    [Fact]
    public async Task QueryAsync_keeps_paging_when_a_full_page_decodes_to_zero_but_limit_is_exceeded()
    {
        // A page whose features all lack a recognized object id decodes to zero
        // records. With the old Records.Count==0 guard the loop would break early
        // and silently truncate; paging on the raw count keeps going.
        const string page1 = """
        {
          "objectIdFieldName": "objectid",
          "exceededTransferLimit": true,
          "features": [ { "attributes": { "site_name": "no oid 1" } }, { "attributes": { "site_name": "no oid 2" } } ]
        }
        """;
        const string page2 = """
        {
          "objectIdFieldName": "objectid",
          "features": [ { "attributes": { "objectid": 7 } } ]
        }
        """;
        var handler = new QueryHandler((HttpStatusCode.OK, page1), (HttpStatusCode.OK, page2));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).QueryAsync(new GeoServicesTarget("http://s", "svc", 9));

        Assert.True(result.Success);
        Assert.Equal(2, handler.CapturedUris.Count); // did not break early on the all-dropped page
        Assert.Contains("resultOffset=2", handler.CapturedUris[1].AbsoluteUri);
        Assert.Equal([7L], result.Records.Select(r => r.ObjectId));
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

    [Fact]
    public async Task QueryAsync_surfaces_transport_exception_as_failure()
    {
        using var http = new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused")));

        var result = await new GeoServicesFeatureSync(http).QueryAsync(new GeoServicesTarget("http://s", "svc", 9));

        Assert.False(result.Success);
        Assert.Contains("connection refused", result.Error);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task QueryAsync_surfaces_invalid_json_body()
    {
        var handler = new QueryHandler((HttpStatusCode.OK, "this is not json"));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).QueryAsync(new GeoServicesTarget("http://s", "svc", 9));

        Assert.False(result.Success);
        Assert.Contains("Invalid response", result.Error);
    }

    [Fact]
    public async Task QueryAsync_blank_where_defaults_to_all_features()
    {
        var handler = new QueryHandler((HttpStatusCode.OK, """{"features":[]}"""));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).QueryAsync(new GeoServicesTarget("http://s", "svc", 9), "   ");

        Assert.True(result.Success);
        Assert.Contains("where=1%3D1", handler.CapturedUris[0].AbsoluteUri); // 1=1
    }

    [Fact]
    public async Task QueryAsync_skips_features_without_attributes_or_object_id_and_decodes_value_kinds()
    {
        const string body = """
        {
          "objectIdFieldName": "fid",
          "features": [
            { "geometry": { "x": 1, "y": 2 } },
            { "attributes": { "site_name": "no oid" } },
            { "attributes": { "fid": 5, "flag": true, "off": false, "missing": null,
                              "rate": 1.25, "tags": [ "a", "b" ] },
              "geometry": { "spatialReference": { "wkid": 4326 } } }
          ]
        }
        """;
        var handler = new QueryHandler((HttpStatusCode.OK, body));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).QueryAsync(new GeoServicesTarget("http://s", "svc", 9));

        Assert.True(result.Success);
        var pulled = Assert.Single(result.Records); // first two are skipped
        Assert.Equal(5, pulled.ObjectId);
        Assert.False(pulled.Record.Values.ContainsKey("fid")); // custom oid field is metadata
        Assert.True((bool)pulled.Record.Values["flag"]!);
        Assert.False((bool)pulled.Record.Values["off"]!);
        Assert.Null(pulled.Record.Values["missing"]);
        Assert.Equal(1.25, (double)pulled.Record.Values["rate"]!, 5);
        Assert.Equal("[\"a\",\"b\"]", ((string)pulled.Record.Values["tags"]!).Replace(" ", "")); // array -> raw text
        Assert.Null(pulled.Record.Location); // geometry without x/y -> null location
    }

    [Fact]
    public async Task SubmitAsync_handles_unexpected_response_with_no_results_array()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"unrelated":true}""");
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FeatureSyncRetryPolicy.None)
            .SubmitAsync(Record(), T);

        Assert.False(result.Success);
        Assert.Contains("no addResults", result.Error);
    }

    [Fact]
    public async Task SubmitAsync_handles_invalid_json_response()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "not json");
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FeatureSyncRetryPolicy.None)
            .SubmitAsync(Record(), T);

        Assert.False(result.Success);
        Assert.Contains("Invalid response", result.Error);
    }

    [Fact]
    public async Task AddAttachmentAsync_surfaces_http_failure_status()
    {
        var handler = new CapturingMultipartHandler(HttpStatusCode.BadGateway, "upstream down");
        using var http = new HttpClient(handler);
        var file = TempFileWithBytes();
        try
        {
            var result = await new GeoServicesFeatureSync(http)
                .AddAttachmentAsync(1, file, "image/jpeg", new GeoServicesTarget("http://s", "svc", 9));

            Assert.False(result.Success);
            Assert.Equal(502, result.ErrorCode);
            Assert.Contains("HTTP 502", result.Error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddAttachmentAsync_handles_unexpected_and_invalid_json_responses()
    {
        var file = TempFileWithBytes();
        try
        {
            using var http1 = new HttpClient(new CapturingMultipartHandler(HttpStatusCode.OK, """{"nope":1}"""));
            var unexpected = await new GeoServicesFeatureSync(http1)
                .AddAttachmentAsync(1, file, "image/jpeg", new GeoServicesTarget("http://s", "svc", 9));
            Assert.False(unexpected.Success);
            Assert.Contains("no addAttachmentResult", unexpected.Error);

            using var http2 = new HttpClient(new CapturingMultipartHandler(HttpStatusCode.OK, "garbage"));
            var invalid = await new GeoServicesFeatureSync(http2)
                .AddAttachmentAsync(1, file, "image/jpeg", new GeoServicesTarget("http://s", "svc", 9));
            Assert.False(invalid.Success);
            Assert.Contains("Invalid response", invalid.Error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void BuildAddsJson_encodes_bool_double_and_non_primitive_attributes()
    {
        var record = new FieldRecord { RecordId = "r", FormId = "f" }; // no location
        record.Values["flag"] = true;
        record.Values["rate"] = 2.5;
        record.Values["when"] = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero); // non-primitive -> string
        record.Values["objectid"] = 99; // reserved key is dropped

        using var doc = JsonDocument.Parse(GeoServicesFeatureSync.BuildAddsJson(record));
        var add = doc.RootElement[0];
        Assert.False(add.TryGetProperty("geometry", out _)); // no location emitted
        var attrs = add.GetProperty("attributes");
        Assert.True(attrs.GetProperty("flag").GetBoolean());
        Assert.Equal(2.5, attrs.GetProperty("rate").GetDouble(), 5);
        Assert.Equal(JsonValueKind.String, attrs.GetProperty("when").ValueKind);
        Assert.False(attrs.TryGetProperty("objectid", out _)); // reserved key not written from values
    }

    [Fact]
    public void BuildAddsJson_validates_null_record()
        => Assert.Throws<ArgumentNullException>(() => GeoServicesFeatureSync.BuildAddsJson(null!));

    [Fact]
    public async Task Submit_and_update_validate_null_arguments()
    {
        using var http = new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}"));
        var sync = new GeoServicesFeatureSync(http);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.SubmitAsync(null!, T));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.SubmitAsync(Record(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.UpdateAsync(1, null!, T));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.UpdateAsync(1, Record(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.DeleteAsync(1, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.QueryAsync(null!));
    }

    [Fact]
    public void Constructor_rejects_null_http_client()
        => Assert.Throws<ArgumentNullException>(() => new GeoServicesFeatureSync(null!));

    [Fact]
    public async Task Retries_transient_transport_exception_then_succeeds()
    {
        var handler = new FlakyHandler(
            new HttpRequestException("reset"),
            (HttpStatusCode.OK, OkUpdate));
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http, FastRetry).UpdateAsync(9, Record(), T);

        Assert.True(result.Success);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw ex;
    }

    // Throws the supplied exception on the first call, then returns the queued responses.
    private sealed class FlakyHandler : HttpMessageHandler
    {
        private readonly Exception _first;
        private readonly (HttpStatusCode Status, string Body)[] _rest;

        public FlakyHandler(Exception first, params (HttpStatusCode Status, string Body)[] rest)
        {
            _first = first;
            _rest = rest;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Calls++;
            if (index == 0)
            {
                throw _first;
            }

            var (status, body) = _rest[Math.Min(index - 1, _rest.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
