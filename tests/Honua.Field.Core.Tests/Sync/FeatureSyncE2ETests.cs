// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;
using Honua.Field.Core.Sync;
using Honua.Field.Presentation.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Tests.Sync;

/// <summary>
/// Comprehensive end-to-end tests against a real running Honua server. Opt-in:
/// every test no-ops unless <c>HONUA_E2E_SERVER</c> is set (e.g.
/// <c>http://localhost:18080</c>); <c>HONUA_E2E_APIKEY</c> supplies the write
/// credential. Exercises the full capture → applyEdits → server → database path
/// the app uses, including update/delete, error cases, and write collisions.
/// Bring a server up with <c>scripts/e2e/up.sh</c>.
/// </summary>
[Collection("e2e")]
public class FeatureSyncE2ETests
{
    private static readonly string? ServerUrl = Environment.GetEnvironmentVariable("HONUA_E2E_SERVER");
    private static readonly string? ApiKey = Environment.GetEnvironmentVariable("HONUA_E2E_APIKEY");
    private const string ServiceId = "mobile_offline_demo";
    private const int LayerId = 68910;

    private static bool Enabled => !string.IsNullOrWhiteSpace(ServerUrl);
    private static GeoServicesTarget Target => new(ServerUrl!, ServiceId, LayerId);

    private static HttpClient Authed(HttpMessageHandler? handler = null)
    {
        var http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = new Uri(ServerUrl!) };
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            http.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
        }

        return http;
    }

    private static FieldRecord Site(string marker, Action<FieldRecord>? customize = null)
    {
        var r = new FieldRecord { RecordId = Guid.NewGuid().ToString("D"), FormId = "field-site", Location = new FieldGeoPoint(21.31, -157.81) };
        r.Values["site_name"] = marker;
        r.Values["status"] = "new";
        r.Values["priority"] = "medium";
        r.Values["offline_action"] = "field-e2e";
        r.Values["inspection_date"] = "2026-05-01T18:00:00Z";
        r.Values["sync_version"] = 1;
        customize?.Invoke(r);
        return r;
    }

    private static string Marker() => "e2e-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>Returns the features (cloned) matching a site_name.</summary>
    private static async Task<JsonElement> QueryByNameAsync(HttpClient http, string name)
    {
        var url = $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query?where=site_name%3D%27{name}%27&outFields=*&f=json";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
        return doc.RootElement.GetProperty("features").Clone();
    }

    private static async Task<int> CountByNameAsync(HttpClient http, string name)
        => (await QueryByNameAsync(http, name)).GetArrayLength();

    /// <summary>Reads an attribute as a long whether returned as a JSON number or numeric string.</summary>
    private static long ReadLong(JsonElement attrs, string name)
    {
        var v = attrs.GetProperty(name);
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.Parse(v.GetString()!, CultureInfo.InvariantCulture);
    }

    // ---- Add / update / delete happy paths -----------------------------------

    [SkippableFact]
    public async Task Lost_add_acknowledgment_survives_reopen_without_creating_a_second_feature()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is required for the live sync test.");

        var marker = Marker();
        var fault = new DropCommittedAddResponse();
        using var uploadHttp = Authed(fault);
        using var readHttp = Authed();
        var sync = new GeoServicesFeatureSync(uploadHttp);
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "field.db");
        const string key = "lost-ack-test-key";

        SyncCenterViewModel OpenCenter(MobileRecordStore store, IReadOnlyList<FieldRecordEntry> entries) =>
            new(entries, (entry, ct) => sync.SubmitAsync(entry.Record, Target, ct), null, null,
                persist: entry => store.SaveAsync(entry),
                batchUploader: (batch, ct) => sync.SubmitBatchAsync(batch.Select(entry => entry.Record).ToArray(), Target, ct));

        try
        {
            var store = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var record = Site(marker, r => r.Status = RecordStatus.Submitted);
            var entry = new FieldRecordEntry(record);
            entry.MarkPending();
            await store.SaveAsync(entry);

            Assert.Equal(0, await OpenCenter(store, [entry]).SyncAsync());
            Assert.NotNull(fault.CommittedObjectId);
            var requestsAfterCommit = fault.EditRequests;

            var reopened = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var entries = await reopened.LoadAllAsync();
            var held = Assert.Single(entries);
            Assert.Equal(record.RecordId, held.Record.RecordId);
            Assert.Equal(marker, held.Record.Values["site_name"]?.ToString());
            Assert.True(held.RequiresServerVerification);
            var secondPass = await OpenCenter(reopened, entries).SyncAsync();

            var feature = Assert.Single((await QueryByNameAsync(readHttp, marker)).EnumerateArray());
            Assert.Equal(fault.CommittedObjectId, ReadLong(feature.GetProperty("attributes"), "objectid"));
            Assert.Equal(0, secondPass);
            Assert.Equal(requestsAfterCommit, fault.EditRequests);
            Assert.False(held.IsPendingUpload);
        }
        finally
        {
            directory.Delete(recursive: true);
            var cleanup = new GeoServicesFeatureSync(readHttp);
            foreach (var feature in (await QueryByNameAsync(readHttp, marker)).EnumerateArray())
            {
                var deleted = await cleanup.DeleteAsync(ReadLong(feature.GetProperty("attributes"), "objectid"), Target);
                Assert.True(deleted.Success, deleted.Error);
            }
        }
    }

    // Deliver all protocol errors normally. Drop the first actual success only after the server
    // returns the assigned ID, as happens when the response is lost on the way back to Field.
    private sealed class DropCommittedAddResponse() : DelegatingHandler(new HttpClientHandler())
    {
        public long? CommittedObjectId { get; private set; }
        public int EditRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri?.AbsolutePath.EndsWith("/applyEdits", StringComparison.OrdinalIgnoreCase) != true)
            {
                return response;
            }

            EditRequests++;
            if (CommittedObjectId is null && response.IsSuccessStatusCode)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (body.RootElement.TryGetProperty("addResults", out var results) && results.GetArrayLength() == 1
                    && results[0].GetProperty("success").GetBoolean())
                {
                    CommittedObjectId = ReadLong(results[0], "objectId");
                    response.Dispose();
                    throw new HttpRequestException("The committed response was lost.");
                }
            }

            return response;
        }
    }

    [SkippableFact]
    public async Task Batch_add_persists_each_feature_once_and_returns_matching_object_ids()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");

        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var markers = new[] { Marker(), Marker() };
        var records = markers.Select((marker, index) => Site(marker, r => r.Values["sync_version"] = index + 1)).ToArray();
        try
        {
            var results = await sync.SubmitBatchAsync(records, Target);

            Assert.Equal(records.Length, results.Count);
            Assert.All(results, result => Assert.True(result.Success, result.Error));
            Assert.NotEqual(results[0].ObjectId, results[1].ObjectId);
            for (var index = 0; index < markers.Length; index++)
            {
                var feature = Assert.Single((await QueryByNameAsync(http, markers[index])).EnumerateArray());
                var attributes = feature.GetProperty("attributes");
                Assert.Equal(ReadLong(attributes, "objectid"), results[index].ObjectId);
                Assert.Equal(index + 1, ReadLong(attributes, "sync_version"));
            }
        }
        finally
        {
            foreach (var marker in markers)
            {
                foreach (var feature in (await QueryByNameAsync(http, marker)).EnumerateArray())
                {
                    var deleted = await sync.DeleteAsync(ReadLong(feature.GetProperty("attributes"), "objectid"), Target);
                    Assert.True(deleted.Success, deleted.Error);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Add_persists_and_round_trips_geometry_and_typed_attributes()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var marker = Marker();
        var record = Site(marker, r => { r.Values["priority"] = "high"; r.Values["sync_version"] = 7; });

        var result = await new GeoServicesFeatureSync(http).SubmitAsync(record, Target);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ObjectId);

        var features = await QueryByNameAsync(http, marker);
        Assert.Equal(1, features.GetArrayLength());
        var f = features[0];
        var attrs = f.GetProperty("attributes");
        Assert.Equal(result.ObjectId, attrs.GetProperty("objectid").GetInt64()); // PK column stays numeric
        Assert.Equal("high", attrs.GetProperty("priority").GetString());
        // The shared 'features' table stores user attributes as JSONB and projects
        // them back as strings, so an integer field round-trips as "7" (read
        // tolerantly rather than assuming the JSON token kind).
        Assert.Equal(7, ReadLong(attrs, "sync_version"));
        Assert.Equal(-157.81, f.GetProperty("geometry").GetProperty("x").GetDouble(), 5);
        Assert.Equal(21.31, f.GetProperty("geometry").GetProperty("y").GetDouble(), 5);
    }

    [SkippableFact]
    public async Task Update_changes_attributes_server_side()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var marker = Marker();

        var added = await sync.SubmitAsync(Site(marker), Target);
        Assert.True(added.Success, added.Error);

        var update = Site(marker, r => { r.Values["status"] = "done"; r.Values["notes"] = "updated"; });
        var updated = await sync.UpdateAsync(added.ObjectId!.Value, update, Target);
        Assert.True(updated.Success, updated.Error);

        var attrs = (await QueryByNameAsync(http, marker))[0].GetProperty("attributes");
        Assert.Equal("done", attrs.GetProperty("status").GetString());
        Assert.Equal("updated", attrs.GetProperty("notes").GetString());
    }

    [SkippableFact]
    public async Task Delete_removes_the_feature()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var marker = Marker();
        var added = await sync.SubmitAsync(Site(marker), Target);
        Assert.True(added.Success, added.Error);
        Assert.Equal(1, await CountByNameAsync(http, marker));

        var deleted = await sync.DeleteAsync(added.ObjectId!.Value, Target);

        Assert.True(deleted.Success, deleted.Error);
        Assert.Equal(0, await CountByNameAsync(http, marker));
    }

    [SkippableFact]
    public async Task Ordinary_attachment_round_trips_metadata_and_photo_bytes()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");

        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        const string fileName = "inspection.png";
        const string contentType = "image/png";
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var sourceBytes = await File.ReadAllBytesAsync(path);
        var added = await sync.SubmitAsync(Site(Marker()), Target);
        Assert.True(added.Success, added.Error);
        Assert.NotNull(added.ObjectId);
        var featureId = added.ObjectId.Value;
        var featureUrl = $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/{featureId}";
        long? attachmentId = null;

        try
        {
            var upload = await sync.AddAttachmentAsync(featureId, path, contentType, Target);
            attachmentId = upload.ObjectId;
            Assert.True(upload.Success, upload.Error);
            Assert.NotNull(attachmentId);

            Assert.Equal(attachmentId.Value,
                await AssertOnlyPhotoAsync(http, featureId, fileName, contentType, sourceBytes));
        }
        finally
        {
            try
            {
                if (attachmentId is long id)
                {
                    using var form = new MultipartFormDataContent
                    {
                        { new StringContent(id.ToString(CultureInfo.InvariantCulture)), "attachmentIds" }
                    };
                    using var response = await http.PostAsync($"{featureUrl}/deleteAttachments", form);
                    response.EnsureSuccessStatusCode();
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var result = Assert.Single(body.RootElement.GetProperty("deleteAttachmentResults").EnumerateArray());
                    Assert.True(result.GetProperty("success").GetBoolean());
                }
            }
            finally
            {
                var deleted = await sync.DeleteAsync(featureId, Target);
                Assert.True(deleted.Success, deleted.Error);
            }
        }
    }

    [SkippableFact]
    public async Task Offline_photo_survives_reopen_and_lost_upload_acknowledgment_without_duplicates()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is required for the live sync test.");

        var marker = Marker();
        var directory = Directory.CreateTempSubdirectory();
        var storePath = Path.Combine(directory.FullName, "field.db");
        const string key = "offline-photo-replay-test-key";
        const string contentType = "image/png";
        var photoId = Guid.NewGuid();
        var fileName = $"{photoId:N}.png";
        var sourceBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "inspection.png"));
        var sha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var fault = new DropCommittedAttachmentResponse();
        using var uploadHttp = Authed(fault);
        using var readHttp = Authed();
        var sync = new GeoServicesFeatureSync(uploadHttp);
        var online = false;
        Exception? replayFailure = null;

        SyncCenterViewModel OpenCenter(MobileRecordStore store, FieldRecordEntry entry) =>
            new([entry], (pending, ct) => sync.SubmitAsync(pending.Record, Target, ct), null, null,
                persist: pending => store.SaveAsync(pending),
                batchUploader: (batch, ct) => sync.SubmitBatchAsync(batch.Select(pending => pending.Record).ToArray(), Target, ct),
                canDispatchOrdinaryAdd: () => online,
                replayOrdinaryPhotos: async pending =>
                {
                    try
                    {
                        return await OrdinaryPhotoReplay.ReplayAsync(
                            pending, sync, Target, directory.FullName, value => store.SaveAsync(value));
                    }
                    catch (Exception ex)
                    {
                        replayFailure = ex;
                        throw;
                    }
                });

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, fileName), sourceBytes);
            var store = await MobileRecordStore.OpenEncryptedAsync(storePath, key);
            var record = Site(marker, item =>
            {
                item.Status = RecordStatus.Submitted;
                item.Location = null;
            });
            var entry = new FieldRecordEntry(record);
            entry.MarkPending();
            entry.AddOrdinaryPhoto(new(photoId, fileName, contentType, fileName, sha256, Target));
            await store.SaveAsync(entry);

            Assert.Equal(0, await OpenCenter(store, entry).SyncAsync());
            Assert.Equal(0, fault.UploadRequests);
            Assert.Equal(0, await CountByNameAsync(readHttp, marker));

            var reopened = await MobileRecordStore.OpenEncryptedAsync(storePath, key);
            var pending = Assert.Single(await reopened.LoadAllAsync());
            Assert.Equal(record.RecordId, pending.Record.RecordId);
            var pendingPhoto = Assert.Single(pending.OrdinaryPhotos);
            Assert.Equal(photoId, pendingPhoto.Id);
            Assert.Equal(OrdinaryPhotoState.Pending, pendingPhoto.State);
            Assert.Equal(SHA256.HashData(sourceBytes),
                SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory.FullName, pendingPhoto.LocalPath))));

            online = true;
            await OpenCenter(reopened, pending).SyncAsync();
            Assert.Null(replayFailure);
            Assert.True(pending.RemoteId is not null, pending.LastError ?? "Feature upload did not return an object ID.");
            Assert.True(pending.HasPendingOrdinaryPhotos);
            Assert.Equal(1, fault.UploadRequests);
            Assert.NotNull(fault.CommittedAttachmentId);
            var feature = Assert.Single((await QueryByNameAsync(readHttp, marker)).EnumerateArray());
            var featureId = ReadLong(feature.GetProperty("attributes"), "objectid");
            Assert.Equal(fault.CommittedAttachmentId.Value,
                await AssertOnlyPhotoAsync(readHttp, featureId, fileName, contentType, sourceBytes));

            reopened = await MobileRecordStore.OpenEncryptedAsync(storePath, key);
            var unconfirmed = Assert.Single(await reopened.LoadAllAsync());
            Assert.Equal(featureId.ToString(CultureInfo.InvariantCulture), unconfirmed.RemoteId);
            var unconfirmedPhoto = Assert.Single(unconfirmed.OrdinaryPhotos);
            Assert.Equal(OrdinaryPhotoState.NeedsVerification, unconfirmedPhoto.State);
            Assert.Null(unconfirmedPhoto.ServerAttachmentId);

            var recoveringCenter = OpenCenter(reopened, unconfirmed);
            Assert.True(recoveringCenter.Summary.HasPendingWork);
            await recoveringCenter.SyncAsync();
            reopened = await MobileRecordStore.OpenEncryptedAsync(storePath, key);
            var recovered = Assert.Single(await reopened.LoadAllAsync());
            Assert.Equal(record.RecordId, recovered.Record.RecordId);
            Assert.False(recovered.IsPendingUpload);
            var recoveredPhoto = Assert.Single(recovered.OrdinaryPhotos);
            Assert.Equal(photoId, recoveredPhoto.Id);
            Assert.Equal(OrdinaryPhotoState.Synced, recoveredPhoto.State);
            Assert.Equal(fault.CommittedAttachmentId, recoveredPhoto.ServerAttachmentId);

            var recoveredCenter = OpenCenter(reopened, recovered);
            Assert.False(recoveredCenter.Summary.HasPendingWork);
            await recoveredCenter.SyncAsync();
            Assert.Equal(1, fault.UploadRequests);
            var finalFeature = Assert.Single((await QueryByNameAsync(readHttp, marker)).EnumerateArray());
            Assert.Equal(featureId, ReadLong(finalFeature.GetProperty("attributes"), "objectid"));
            Assert.Equal(fault.CommittedAttachmentId.Value,
                await AssertOnlyPhotoAsync(readHttp, featureId, fileName, contentType, sourceBytes));
        }
        finally
        {
            directory.Delete(recursive: true);
            var cleanup = new GeoServicesFeatureSync(readHttp);
            foreach (var feature in (await QueryByNameAsync(readHttp, marker)).EnumerateArray())
            {
                var deleted = await cleanup.DeleteAsync(ReadLong(feature.GetProperty("attributes"), "objectid"), Target);
                Assert.True(deleted.Success, deleted.Error);
            }
        }
    }

    private sealed class DropCommittedAttachmentResponse() : DelegatingHandler(new HttpClientHandler())
    {
        public long? CommittedAttachmentId { get; private set; }
        public int UploadRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri?.AbsolutePath.EndsWith("/addAttachment", StringComparison.OrdinalIgnoreCase) != true)
            {
                return response;
            }

            UploadRequests++;
            if (CommittedAttachmentId is null && response.IsSuccessStatusCode)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (body.RootElement.TryGetProperty("addAttachmentResult", out var result)
                    && result.GetProperty("success").GetBoolean())
                {
                    CommittedAttachmentId = ReadLong(result, "objectId");
                    response.Dispose();
                    throw new HttpRequestException("The committed attachment response was lost.");
                }
            }

            return response;
        }
    }

    private static async Task<long> AssertOnlyPhotoAsync(
        HttpClient http, long featureId, string fileName, string contentType, byte[] sourceBytes)
    {
        var featureUrl = $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/{featureId}";
        using var metadata = JsonDocument.Parse(await http.GetStringAsync($"{featureUrl}/attachments?f=json"));
        var info = Assert.Single(metadata.RootElement.GetProperty("attachmentInfos").EnumerateArray());
        var attachmentId = ReadLong(info, "id");
        Assert.Equal(fileName, info.GetProperty("name").GetString());
        Assert.Equal(contentType, info.GetProperty("contentType").GetString());
        Assert.Equal(sourceBytes.LongLength, ReadLong(info, "size"));

        using var download = await http.GetAsync($"{featureUrl}/attachments/{attachmentId}");
        download.EnsureSuccessStatusCode();
        Assert.Equal(contentType, download.Content.Headers.ContentType?.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(SHA256.HashData(sourceBytes), SHA256.HashData(bytes));
        return attachmentId;
    }

    // ---- Error / rejection cases ---------------------------------------------

    [SkippableFact]
    public async Task Update_of_nonexistent_feature_is_rejected_not_crashing()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var result = await new GeoServicesFeatureSync(http).UpdateAsync(999_000_111, Site(Marker()), Target);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Delete_of_nonexistent_feature_succeeds_idempotently()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var result = await new GeoServicesFeatureSync(http).DeleteAsync(999_000_222, Target);

        Assert.True(result.Success, result.Error);
        Assert.Equal(999_000_222L, result.ObjectId);
    }

    [SkippableFact]
    public async Task Submit_without_credentials_fails_gracefully()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var anon = new HttpClient { BaseAddress = new Uri(ServerUrl!) }; // no X-API-Key
        var marker = Marker();
        var result = await new GeoServicesFeatureSync(anon).SubmitAsync(Site(marker), Target);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);

        // Nothing should have been written.
        using var authed = Authed();
        Assert.Equal(0, await CountByNameAsync(authed, marker));
    }

    // ---- Collisions / concurrency --------------------------------------------

    [SkippableFact]
    public async Task Concurrent_adds_all_persist_with_distinct_object_ids()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        const int n = 12;
        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var markers = Enumerable.Range(0, n).Select(_ => Marker()).ToArray();

        // Fire all submissions concurrently — stresses server-side id assignment.
        var results = await Task.WhenAll(markers.Select(m => sync.SubmitAsync(Site(m), Target)));

        Assert.All(results, r => Assert.True(r.Success, r.Error));
        var ids = results.Select(r => r.ObjectId).ToHashSet();
        Assert.Equal(n, ids.Count); // no object-id collision / lost write

        // Every one is independently retrievable server-side.
        foreach (var marker in markers)
        {
            Assert.Equal(1, await CountByNameAsync(http, marker));
        }
    }

    [SkippableFact]
    public async Task Concurrent_updates_to_same_feature_all_succeed_via_retry()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        const int n = 10;
        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http); // default retry policy
        var marker = Marker();

        var added = await sync.SubmitAsync(Site(marker), Target);
        Assert.True(added.Success, added.Error);
        var oid = added.ObjectId!.Value;

        // N concurrent writers each set notes to a distinct value — a write
        // collision on a single row. Transient contention is retried, so every
        // submission succeeds rather than surfacing a spurious failure.
        var values = Enumerable.Range(0, n).Select(i => $"writer-{i}").ToArray();
        var results = await Task.WhenAll(values.Select(v =>
            sync.UpdateAsync(oid, Site(marker, r => r.Values["notes"] = v), Target)));

        Assert.All(results, r => Assert.True(r.Success, r.Error)); // retry resolves the contention

        // Consistency: exactly one row remains (no duplication) and its value
        // comes from a writer that actually submitted it (last-write-wins, intact).
        var features = await QueryByNameAsync(http, marker);
        Assert.Equal(1, features.GetArrayLength());
        var finalNotes = features[0].GetProperty("attributes").GetProperty("notes").GetString();
        Assert.Contains(finalNotes, values);
    }

    [SkippableFact]
    public async Task Separate_records_with_identical_capture_data_create_distinct_rows()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var marker = Marker();

        var r1 = await sync.SubmitAsync(Site(marker), Target);
        var r2 = await sync.SubmitAsync(Site(marker), Target);

        Assert.True(r1.Success, r1.Error);
        Assert.True(r2.Success, r2.Error);
        Assert.NotEqual(r1.ObjectId, r2.ObjectId);
        Assert.Equal(2, await CountByNameAsync(http, marker));
    }
}

/// <summary>Serializes the e2e tests so concurrent classes don't fight over the server.</summary>
[CollectionDefinition("e2e", DisableParallelization = true)]
public sealed class E2ECollection;
