using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Honua.Collect.Core.Tests.Migration;

/// <summary>
/// An in-memory reference ArcGIS/GeoServices FeatureServer used to prove Esri
/// interop without a live ArcGIS server (epic #37). It is an
/// <see cref="HttpMessageHandler"/> that mirrors the Esri JSON wire contracts the
/// <see cref="Core.Sync.GeoServicesFeatureSync"/> client speaks —
/// <c>generateToken</c>, layer <c>query</c>, <c>applyEdits</c>
/// (adds/updates/deletes), and <c>addAttachment</c> — over an in-memory feature
/// table. It is deliberately faithful to Esri's quirks: object ids are
/// server-assigned and monotonic, <c>query</c> pages via
/// <c>resultOffset</c>/<c>resultRecordCount</c> and reports
/// <c>exceededTransferLimit</c>, geometry is <c>{x,y}</c> with a
/// <c>spatialReference.wkid</c>, and per-edit results carry <c>success</c>/<c>error</c>.
/// </summary>
internal sealed class FakeGeoServicesServer : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Dictionary<string, object?>> _features = new();
    private readonly Dictionary<long, List<Attachment>> _attachments = new();
    private long _nextObjectId = 1000;
    private long _nextAttachmentId = 1;

    /// <summary>The feature-table column the server treats as the primary key.</summary>
    public string ObjectIdField { get; init; } = "objectid";

    /// <summary>When set, requests without this token in <c>generateToken</c>-issued auth are rejected.</summary>
    public string? RequiredToken { get; set; }

    /// <summary>The maximum features a single query page returns before paging.</summary>
    public int MaxRecordCount { get; init; } = 1000;

    /// <summary>Credentials accepted by <c>generateToken</c> (username → password).</summary>
    public Dictionary<string, string> Credentials { get; } = new(StringComparer.Ordinal);

    /// <summary>Snapshot of the stored feature attributes by object id (test assertions).</summary>
    public IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> Features
    {
        get
        {
            lock (_gate)
            {
                return _features.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(kv.Value));
            }
        }
    }

    /// <summary>Attachments uploaded for a feature (test assertions).</summary>
    public IReadOnlyList<Attachment> AttachmentsFor(long objectId)
    {
        lock (_gate)
        {
            return _attachments.TryGetValue(objectId, out var list) ? [.. list] : [];
        }
    }

    /// <summary>Seeds a feature directly (server-side starting state).</summary>
    public long Seed(IReadOnlyDictionary<string, object?> attributes, double? x = null, double? y = null)
    {
        lock (_gate)
        {
            var oid = _nextObjectId++;
            var stored = new Dictionary<string, object?>(attributes, StringComparer.OrdinalIgnoreCase)
            {
                [ObjectIdField] = oid,
            };
            if (x is { } lx && y is { } ly)
            {
                stored["__x"] = lx;
                stored["__y"] = ly;
            }

            _features[oid] = stored;
            return oid;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var isMultipart = request.Content is { } c && (c.Headers.ContentType?.MediaType?.Contains("multipart") ?? false);
        var body = !isMultipart && request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : string.Empty;
        var form = ParseFormUrlEncoded(body);

        if (path.EndsWith("/generateToken", StringComparison.Ordinal))
        {
            return Json(HandleGenerateToken(form));
        }

        if (RequiredToken is not null && !HasValidToken(request, form))
        {
            return Json("""{"error":{"code":499,"message":"Token Required"}}""", HttpStatusCode.OK);
        }

        if (path.EndsWith("/query", StringComparison.Ordinal))
        {
            var query = ParseFormUrlEncoded(request.RequestUri.Query.TrimStart('?'));
            return Json(HandleQuery(query));
        }

        if (path.EndsWith("/applyEdits", StringComparison.Ordinal))
        {
            return Json(HandleApplyEdits(form));
        }

        if (path.EndsWith("/addAttachment", StringComparison.Ordinal))
        {
            var oid = ParseAttachmentObjectId(path);
            return Json(await HandleAddAttachmentAsync(oid, request, cancellationToken));
        }

        return Json("""{"error":{"code":404,"message":"Unknown endpoint"}}""");
    }

    private static Dictionary<string, string?> ParseFormUrlEncoded(string body)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(body))
        {
            return result;
        }

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> form, string key)
        => form.TryGetValue(key, out var v) ? v : null;

    private bool HasValidToken(HttpRequestMessage request, IReadOnlyDictionary<string, string?> form)
    {
        var headerToken = request.Headers.Authorization?.Parameter;
        var formToken = Get(form, "token");
        var apiKey = request.Headers.TryGetValues("X-API-Key", out var keys) ? keys.FirstOrDefault() : null;
        return RequiredToken == headerToken || RequiredToken == formToken || RequiredToken == apiKey;
    }

    private string HandleGenerateToken(IReadOnlyDictionary<string, string?> form)
    {
        var username = Get(form, "username");
        var password = Get(form, "password");
        if (username is not null && Credentials.TryGetValue(username, out var expected) && expected == password)
        {
            var token = RequiredToken ?? "tok-" + Guid.NewGuid().ToString("n")[..12];
            RequiredToken = token; // bind the issued token as the accepted one
            var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
            return $$"""{"token":"{{token}}","expires":{{expires.ToString(CultureInfo.InvariantCulture)}},"ssl":true}""";
        }

        return """{"error":{"code":400,"message":"Unable to generate token.","details":["Invalid username or password."]}}""";
    }

    private string HandleQuery(IReadOnlyDictionary<string, string?> query)
    {
        var offset = int.TryParse(Get(query, "resultOffset"), out var o) ? o : 0;
        var requested = int.TryParse(Get(query, "resultRecordCount"), out var rc) ? rc : MaxRecordCount;
        var pageSize = Math.Min(requested, MaxRecordCount);
        var where = Get(query, "where");

        lock (_gate)
        {
            var ordered = _features.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            var filtered = ordered.Where(f => MatchesWhere(f, where)).ToList();
            var page = filtered.Skip(offset).Take(pageSize).ToList();
            var exceeded = filtered.Count > offset + page.Count;

            var sb = new StringBuilder();
            sb.Append("{\"objectIdFieldName\":\"").Append(ObjectIdField).Append("\",");
            sb.Append("\"geometryType\":\"esriGeometryPoint\",");
            sb.Append("\"spatialReference\":{\"wkid\":4326},");
            if (exceeded)
            {
                sb.Append("\"exceededTransferLimit\":true,");
            }

            sb.Append("\"features\":[");
            for (var i = 0; i < page.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                WriteFeature(sb, page[i]);
            }

            sb.Append("]}");
            return sb.ToString();
        }
    }

    private void WriteFeature(StringBuilder sb, Dictionary<string, object?> feature)
    {
        sb.Append("{\"attributes\":{");
        var first = true;
        foreach (var (key, value) in feature)
        {
            if (key is "__x" or "__y")
            {
                continue;
            }

            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(JsonSerializer.Serialize(key)).Append(':').Append(JsonSerializer.Serialize(value));
        }

        sb.Append('}');

        if (feature.TryGetValue("__x", out var xv) && feature.TryGetValue("__y", out var yv)
            && xv is double x && yv is double y)
        {
            sb.Append(",\"geometry\":{\"x\":")
                .Append(x.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"y\":")
                .Append(y.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"spatialReference\":{\"wkid\":4326}}");
        }

        sb.Append('}');
    }

    private static bool MatchesWhere(Dictionary<string, object?> feature, string? where)
    {
        if (string.IsNullOrWhiteSpace(where) || where.Trim() == "1=1")
        {
            return true;
        }

        // Support the single "field='value'" equality the client/tests use.
        var idx = where.IndexOf('=');
        if (idx <= 0)
        {
            return true;
        }

        var field = where[..idx].Trim();
        var rhs = where[(idx + 1)..].Trim().Trim('\'');
        return feature.TryGetValue(field, out var actual)
            && string.Equals(actual?.ToString(), rhs, StringComparison.Ordinal);
    }

    private string HandleApplyEdits(IReadOnlyDictionary<string, string?> form)
    {
        var adds = ParseFeatures(Get(form, "adds"));
        var updates = ParseFeatures(Get(form, "updates"));
        var deletes = ParseDeletes(Get(form, "deletes"));

        lock (_gate)
        {
            var addResults = new List<string>();
            foreach (var (attributes, x, y) in adds)
            {
                var oid = _nextObjectId++;
                attributes[ObjectIdField] = oid;
                if (x is { } px && y is { } py)
                {
                    attributes["__x"] = px;
                    attributes["__y"] = py;
                }

                _features[oid] = attributes;
                addResults.Add($$"""{"objectId":{{oid}},"success":true}""");
            }

            var updateResults = new List<string>();
            foreach (var (attributes, x, y) in updates)
            {
                if (!attributes.TryGetValue(ObjectIdField, out var oidObj) || oidObj is not long oid || !_features.ContainsKey(oid))
                {
                    updateResults.Add("""{"success":false,"error":{"code":1000,"description":"Feature not found."}}""");
                    continue;
                }

                var stored = _features[oid];
                foreach (var (k, v) in attributes)
                {
                    stored[k] = v;
                }

                if (x is { } px && y is { } py)
                {
                    stored["__x"] = px;
                    stored["__y"] = py;
                }

                updateResults.Add($$"""{"objectId":{{oid}},"success":true}""");
            }

            var deleteResults = new List<string>();
            foreach (var oid in deletes)
            {
                if (_features.Remove(oid))
                {
                    deleteResults.Add($$"""{"objectId":{{oid}},"success":true}""");
                }
                else
                {
                    deleteResults.Add("{\"objectId\":" + oid + ",\"success\":false,\"error\":{\"code\":1000,\"description\":\"Feature not found.\"}}");
                }
            }

            return $$"""
                {"addResults":[{{string.Join(",", addResults)}}],"updateResults":[{{string.Join(",", updateResults)}}],"deleteResults":[{{string.Join(",", deleteResults)}}]}
                """;
        }
    }

    private async Task<string> HandleAddAttachmentAsync(long objectId, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool exists;
        lock (_gate)
        {
            exists = _features.ContainsKey(objectId);
        }

        if (!exists)
        {
            return """{"error":{"code":1000,"message":"Feature not found."}}""";
        }

        var fileName = "attachment";
        var size = 0;
        if (request.Content is System.Net.Http.MultipartFormDataContent multipart)
        {
            foreach (var part in multipart)
            {
                if (part.Headers.ContentDisposition?.Name?.Trim('"') == "attachment")
                {
                    fileName = part.Headers.ContentDisposition.FileName?.Trim('"') ?? fileName;
                    size = (await part.ReadAsByteArrayAsync(cancellationToken)).Length;
                }
            }
        }

        long attachmentId;
        lock (_gate)
        {
            attachmentId = _nextAttachmentId++;
            if (!_attachments.TryGetValue(objectId, out var list))
            {
                _attachments[objectId] = list = [];
            }

            list.Add(new Attachment(attachmentId, fileName, size));
        }

        return "{\"addAttachmentResult\":{\"objectId\":" + attachmentId + ",\"success\":true}}";
    }

    private List<(Dictionary<string, object?> Attributes, double? X, double? Y)> ParseFeatures(string? json)
    {
        var result = new List<(Dictionary<string, object?>, double?, double?)>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        using var doc = JsonDocument.Parse(json);
        foreach (var feature in doc.RootElement.EnumerateArray())
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (feature.TryGetProperty("attributes", out var attrs))
            {
                foreach (var attr in attrs.EnumerateObject())
                {
                    attributes[attr.Name] = Convert(attr.Value);
                }
            }

            double? x = null, y = null;
            if (feature.TryGetProperty("geometry", out var geom) && geom.ValueKind == JsonValueKind.Object
                && geom.TryGetProperty("x", out var gx) && gx.TryGetDouble(out var dx)
                && geom.TryGetProperty("y", out var gy) && gy.TryGetDouble(out var dy))
            {
                x = dx;
                y = dy;
            }

            result.Add((attributes, x, y));
        }

        return result;
    }

    private static List<long> ParseDeletes(string? json)
    {
        var result = new List<long>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        using var doc = JsonDocument.Parse(json);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetInt64(out var oid))
            {
                result.Add(oid);
            }
        }

        return result;
    }

    private static object? Convert(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        // Box each numeric branch separately: a unified ternary widens the integer
        // branch to double, so an object-id read back as double won't match a long key.
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : (object)value.GetDouble(),
        _ => value.GetRawText(),
    };

    private static long ParseAttachmentObjectId(string path)
    {
        // .../FeatureServer/{layer}/{objectId}/addAttachment
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var addIdx = Array.IndexOf(segments, "addAttachment");
        return addIdx > 0 && long.TryParse(segments[addIdx - 1], out var oid) ? oid : -1;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An attachment uploaded against a feature.</summary>
    /// <param name="AttachmentId">Server-assigned attachment id.</param>
    /// <param name="FileName">Original file name.</param>
    /// <param name="SizeBytes">Uploaded byte count.</param>
    internal sealed record Attachment(long AttachmentId, string FileName, int SizeBytes);
}
