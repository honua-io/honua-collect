using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Migration;

/// <summary>
/// Proves ArcGIS FeatureServer round-trip interop (epic #37) by exercising the real
/// <see cref="GeoServicesFeatureSync"/> client against an in-memory reference
/// FeatureServer (<see cref="FakeGeoServicesServer"/>) that mirrors Esri's
/// <c>generateToken</c> / <c>query</c> / <c>applyEdits</c> / <c>addAttachment</c>
/// JSON contracts — no live ArcGIS server required. This complements
/// <c>FeatureSyncE2ETests</c> (opt-in, real server) with an always-on interop proof.
/// </summary>
public sealed class GeoServicesRoundTripTests
{
    private static readonly GeoServicesTarget Target = new("https://arcgis.example.com", "field_sites", 0);

    private static HttpClient Client(FakeGeoServicesServer server, string? token = null)
    {
        var http = new HttpClient(server) { BaseAddress = new Uri("https://arcgis.example.com") };
        if (token is not null)
        {
            http.DefaultRequestHeaders.Add("X-API-Key", token);
        }

        return http;
    }

    private static FieldRecord Site(string name, Action<FieldRecord>? customize = null)
    {
        var r = new FieldRecord { RecordId = name, FormId = "field_sites", Location = new FieldGeoPoint(21.31, -157.81) };
        r.Values["site_name"] = name;
        r.Values["status"] = "new";
        customize?.Invoke(r);
        return r;
    }

    [Fact]
    public async Task Add_then_query_round_trips_attributes_and_geometry()
    {
        var server = new FakeGeoServicesServer();
        using var http = Client(server);
        var sync = new GeoServicesFeatureSync(http);

        var add = await sync.SubmitAsync(Site("alpha", r => { r.Values["priority"] = "high"; r.Values["count"] = 7; }), Target);
        Assert.True(add.Success, add.Error);
        Assert.NotNull(add.ObjectId);

        var query = await sync.QueryAsync(Target);
        Assert.True(query.Success, query.Error);
        var pulled = Assert.Single(query.Records);

        Assert.Equal(add.ObjectId, pulled.ObjectId);
        Assert.Equal("alpha", pulled.Record.Values["site_name"]);
        Assert.Equal("high", pulled.Record.Values["priority"]);
        Assert.Equal(7L, pulled.Record.Values["count"]); // integer stays integral on the round trip
        Assert.NotNull(pulled.Record.Location);
        Assert.Equal(21.31, pulled.Record.Location!.Latitude, 5);
        Assert.Equal(-157.81, pulled.Record.Location.Longitude, 5);
    }

    [Fact]
    public async Task Update_changes_server_state_and_is_visible_to_query()
    {
        var server = new FakeGeoServicesServer();
        using var http = Client(server);
        var sync = new GeoServicesFeatureSync(http);

        var add = await sync.SubmitAsync(Site("bravo"), Target);
        var update = await sync.UpdateAsync(add.ObjectId!.Value, Site("bravo", r => r.Values["status"] = "done"), Target);
        Assert.True(update.Success, update.Error);

        var query = await sync.QueryAsync(Target, "site_name='bravo'");
        var pulled = Assert.Single(query.Records);
        Assert.Equal("done", pulled.Record.Values["status"]);
    }

    [Fact]
    public async Task Delete_removes_the_feature()
    {
        var server = new FakeGeoServicesServer();
        using var http = Client(server);
        var sync = new GeoServicesFeatureSync(http);

        var add = await sync.SubmitAsync(Site("charlie"), Target);
        var delete = await sync.DeleteAsync(add.ObjectId!.Value, Target);
        Assert.True(delete.Success, delete.Error);

        var query = await sync.QueryAsync(Target);
        Assert.Empty(query.Records);
    }

    [Fact]
    public async Task Update_of_nonexistent_feature_is_rejected_not_thrown()
    {
        var server = new FakeGeoServicesServer();
        using var http = Client(server);

        var result = await new GeoServicesFeatureSync(http, FeatureSyncRetryPolicy.None)
            .UpdateAsync(999_999, Site("ghost"), Target);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddAttachment_uploads_to_the_feature()
    {
        var server = new FakeGeoServicesServer();
        using var http = Client(server);
        var sync = new GeoServicesFeatureSync(http);

        var add = await sync.SubmitAsync(Site("delta"), Target);
        var file = Path.Combine(Path.GetTempPath(), $"interop-{Guid.NewGuid():n}.jpg");
        await File.WriteAllBytesAsync(file, [0x01, 0x02, 0x03, 0x04, 0x05]);
        try
        {
            var attach = await sync.AddAttachmentAsync(add.ObjectId!.Value, file, "image/jpeg", Target);
            Assert.True(attach.Success, attach.Error);
            Assert.NotNull(attach.ObjectId);

            var stored = Assert.Single(server.AttachmentsFor(add.ObjectId.Value));
            Assert.Equal(5, stored.SizeBytes);
            Assert.EndsWith(".jpg", stored.FileName);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Query_follows_server_paging_to_pull_all_features()
    {
        var server = new FakeGeoServicesServer { MaxRecordCount = 3 };
        using var http = Client(server);
        var sync = new GeoServicesFeatureSync(http);

        for (var i = 0; i < 7; i++)
        {
            var add = await sync.SubmitAsync(Site($"site-{i}"), Target);
            Assert.True(add.Success, add.Error);
        }

        var query = await sync.QueryAsync(Target);
        Assert.True(query.Success, query.Error);
        Assert.Equal(7, query.Records.Count); // paged 3+3+1, fully drained
    }

    [Fact]
    public async Task Full_lifecycle_add_attachment_update_query_delete()
    {
        var server = new FakeGeoServicesServer();
        using var http = Client(server);
        var sync = new GeoServicesFeatureSync(http);

        // add
        var add = await sync.SubmitAsync(Site("echo", r => r.Values["count"] = 1), Target);
        var oid = add.ObjectId!.Value;

        // attachment
        var file = Path.Combine(Path.GetTempPath(), $"interop-{Guid.NewGuid():n}.png");
        await File.WriteAllBytesAsync(file, new byte[64]);
        try
        {
            Assert.True((await sync.AddAttachmentAsync(oid, file, "image/png", Target)).Success);
        }
        finally
        {
            File.Delete(file);
        }

        // update
        Assert.True((await sync.UpdateAsync(oid, Site("echo", r => r.Values["count"] = 2), Target)).Success);

        // query reflects the update
        var pulled = Assert.Single((await sync.QueryAsync(Target, "site_name='echo'")).Records);
        Assert.Equal(2L, pulled.Record.Values["count"]);

        // delete
        Assert.True((await sync.DeleteAsync(oid, Target)).Success);
        Assert.Empty((await sync.QueryAsync(Target)).Records);
    }
}
