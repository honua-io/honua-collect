// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Honua.Field.Core.Records;
using Honua.Field.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Tests.Sync;

public sealed class OrdinaryPhotoReplayTests : IDisposable
{
    private static readonly GeoServicesTarget Target = new("https://example.test", "sites", 0);
    private readonly DirectoryInfo _media = Directory.CreateTempSubdirectory();

    [Fact]
    public async Task Forbidden_attachment_returns_to_pending_and_can_upload_on_next_pass()
    {
        using var server = new AttachmentHandler(HttpStatusCode.Forbidden);
        using var http = new HttpClient(server);
        var entry = CreateEntry();
        var checkpoints = new List<OrdinaryPhotoState>();

        Task Persist(FieldRecordEntry current)
        {
            checkpoints.Add(Assert.Single(current.OrdinaryPhotos).State);
            return Task.CompletedTask;
        }

        Assert.False(await ReplayAsync(entry, http, Persist));
        Assert.Equal(OrdinaryPhotoState.Pending, Assert.Single(entry.OrdinaryPhotos).State);
        Assert.Equal(1, server.Posts);
        Assert.True(await ReplayAsync(entry, http, Persist));

        var photo = Assert.Single(entry.OrdinaryPhotos);
        Assert.Equal(OrdinaryPhotoState.Synced, photo.State);
        Assert.Equal(123L, photo.ServerAttachmentId);
        Assert.Equal(2, server.Posts);
        Assert.Equal(0, server.Lists);
        Assert.Equal(
            [OrdinaryPhotoState.NeedsVerification, OrdinaryPhotoState.Pending,
                OrdinaryPhotoState.NeedsVerification, OrdinaryPhotoState.Synced],
            checkpoints);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.Conflict, true)]
    public async Task Ambiguous_structured_error_verifies_without_posting_again(HttpStatusCode status, bool nestedError)
    {
        using var server = new AttachmentHandler(status, nestedError);
        using var http = new HttpClient(server);
        var entry = CreateEntry();

        Assert.False(await ReplayAsync(entry, http, _ => Task.CompletedTask));
        Assert.False(await ReplayAsync(entry, http, _ => Task.CompletedTask));

        var photo = Assert.Single(entry.OrdinaryPhotos);
        Assert.Equal(OrdinaryPhotoState.NeedsVerification, photo.State);
        Assert.Null(photo.ServerAttachmentId);
        Assert.Equal(1, server.Posts);
        Assert.Equal(1, server.Lists);
    }

    [Theory]
    [InlineData(1, OrdinaryPhotoState.Pending, 0)]
    [InlineData(2, OrdinaryPhotoState.NeedsVerification, 1)]
    public async Task Failed_checkpoint_restores_last_persisted_photo(
        int failingCheckpoint, OrdinaryPhotoState expectedState, int expectedPosts)
    {
        using var server = new AttachmentHandler(HttpStatusCode.OK);
        using var http = new HttpClient(server);
        var entry = CreateEntry();
        var original = Assert.Single(entry.OrdinaryPhotos);
        var proposedStates = new List<OrdinaryPhotoState>();
        var failure = new IOException("Photo checkpoint could not be saved.");

        Task Persist(FieldRecordEntry current)
        {
            proposedStates.Add(Assert.Single(current.OrdinaryPhotos).State);
            return proposedStates.Count == failingCheckpoint ? Task.FromException(failure) : Task.CompletedTask;
        }

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ReplayAsync(entry, http, Persist)));
        Assert.Equal(original with { State = expectedState }, Assert.Single(entry.OrdinaryPhotos));
        Assert.Equal(expectedPosts, server.Posts);
        Assert.Equal(OrdinaryPhotoState.NeedsVerification, proposedStates[0]);
        if (failingCheckpoint == 2)
        {
            Assert.Equal(OrdinaryPhotoState.Synced, proposedStates[1]);
            Assert.False(await ReplayAsync(entry, http, _ => Task.CompletedTask));
            Assert.Equal(expectedPosts, server.Posts);
            Assert.Equal(1, server.Lists);
        }
    }

    private FieldRecordEntry CreateEntry()
    {
        var id = Guid.NewGuid();
        var name = $"{id:N}.png";
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "inspection.png"));
        File.WriteAllBytes(Path.Combine(_media.FullName, name), bytes);
        var entry = new FieldRecordEntry(new FieldRecord { RecordId = Guid.NewGuid().ToString(), FormId = "field-site" });
        entry.MarkSynced("42");
        entry.AddOrdinaryPhoto(new OrdinaryFeaturePhoto(id, name, "image/png", name, Convert.ToHexString(SHA256.HashData(bytes)), Target));
        return entry;
    }

    private Task<bool> ReplayAsync(FieldRecordEntry entry, HttpClient http, Func<FieldRecordEntry, Task> persist)
        => OrdinaryPhotoReplay.ReplayAsync(entry, new GeoServicesFeatureSync(http), Target, _media.FullName, persist);

    public void Dispose() => _media.Delete(recursive: true);

    private sealed class AttachmentHandler(HttpStatusCode firstUploadStatus, bool nestedError = false) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public int Lists { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/42/addAttachment", request.RequestUri!.AbsolutePath);
                Assert.IsType<MultipartFormDataContent>(request.Content);
                Posts++;
                var status = Posts == 1 ? firstUploadStatus : HttpStatusCode.OK;
                if (nestedError && status != HttpStatusCode.OK)
                {
                    return Task.FromResult(Response(HttpStatusCode.OK,
                        $$$$"""{"addAttachmentResult":{"success":false,"error":{"code":{{{{(int)status}}}},"message":"Upload failed."}}}"""));
                }

                var json = status == HttpStatusCode.OK
                    ? """{"addAttachmentResult":{"success":true,"objectId":123}}"""
                    : $$$"""{"error":{"code":{{{(int)status}}},"message":"Upload failed."}}""";
                return Task.FromResult(Response(status, json));
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/42/attachments", request.RequestUri!.AbsolutePath);
            Lists++;
            return Task.FromResult(Response(HttpStatusCode.OK, """{"attachmentInfos":[]}"""));
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
