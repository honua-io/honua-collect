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

        var result = await new GeoServicesFeatureSync(http).UpdateAsync(99, Record(), new GeoServicesTarget("http://s", "svc", 9));

        Assert.False(result.Success);
        Assert.Equal("Feature not found.", result.Error);
    }
}
