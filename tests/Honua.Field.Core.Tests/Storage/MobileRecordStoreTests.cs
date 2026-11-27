// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;
using Honua.Field.Core.Sync;
using Honua.Mobile.Offline.GeoPackage;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Field.Core.Tests.Storage;

public sealed class MobileRecordStoreTests
{
    [Fact]
    public async Task Failed_import_rolls_back_records_and_marker_and_restarts_with_same_key()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "records.db");
            const string key = "device-test-key";
            var legacy = await SqliteRecordStore.OpenEncryptedAsync(path, key);
            await legacy.SaveAsync(new FieldRecordEntry(CreateRecord("draft")));
            var submission = new PublishedFormSubmission("inspection", """{"idempotencyKey":"pending"}""", []);
            var pendingRecord = CreateRecord("pending");
            pendingRecord.Status = RecordStatus.Submitted;
            await new RecordBook(legacy).AddSubmittedAsync(pendingRecord, submission);

            var sdk = new GeoPackageSyncStore(new GeoPackageSyncStoreOptions { DatabasePath = path, EncryptionKey = key });
            await sdk.InitializeAsync();
            await ExecuteAsync("""
                CREATE TRIGGER fail_second_import BEFORE INSERT ON honua_local_documents
                WHEN EXISTS (SELECT 1 FROM honua_local_documents)
                BEGIN
                    SELECT RAISE(ABORT, 'test interrupted import');
                END;
                """);
            await Assert.ThrowsAsync<SqliteException>(() => MobileRecordStore.OpenEncryptedAsync(path, key));
            Assert.False(await sdk.IsDocumentCollectionImportedAsync("honua-field-records-v1"));
            Assert.Empty(await sdk.LoadDocumentsAsync("honua-field-records-v1"));
            Assert.Equal(2, (await legacy.LoadAllAsync()).Count);
            await ExecuteAsync("DROP TRIGGER fail_second_import;");

            // The preceding trigger changes can still reside in WAL. Checkpoint them
            // before measuring whether a failed open changes any persisted data.
            await ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            var bytes = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MobileRecordStore.OpenEncryptedAsync(path, "wrong-key"));
            await Assert.ThrowsAsync<ArgumentException>(() => MobileRecordStore.OpenEncryptedAsync(path, ""));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            var wal = new FileInfo(path + "-wal");
            Assert.True(!wal.Exists || wal.Length == 0);
            Assert.Equal(2, (await legacy.LoadAllAsync()).Count);
            Assert.False(await sdk.IsDocumentCollectionImportedAsync("honua-field-records-v1"));

            var store = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var pending = Assert.Single(await store.LoadAllAsync(), entry => entry.SyncState == RecordSyncState.Pending);
            Assert.Equal(submission.RequestJson, pending.PublishedSubmission?.RequestJson);
            Assert.Equal("pending", pending.Record.RecordId);
            Assert.True(await sdk.IsDocumentCollectionImportedAsync("honua-field-records-v1"));

            await ExecuteAsync("""
                CREATE TRIGGER fail_outbox_write BEFORE UPDATE ON honua_local_documents
                BEGIN
                    SELECT RAISE(ABORT, 'test interrupted outbox write');
                END;
                """);
            var submittedDraft = CreateRecord("draft");
            submittedDraft.Status = RecordStatus.Submitted;
            var correctedSubmission = new PublishedFormSubmission("inspection", """{"idempotencyKey":"draft"}""", []);
            await Assert.ThrowsAsync<SqliteException>(() => new RecordBook(store).AddSubmittedAsync(submittedDraft, correctedSubmission));
            var reopened = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var original = Assert.Single(await reopened.LoadAllAsync(), entry => entry.Record.RecordId == "draft");
            Assert.Equal(RecordStatus.Draft, original.Record.Status);
            Assert.Null(original.PublishedSubmission);

            await ExecuteAsync("DROP TRIGGER fail_outbox_write;");
            await new RecordBook(reopened).AddSubmittedAsync(submittedDraft, correctedSubmission);
            var restarted = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var queued = Assert.Single(await restarted.LoadAllAsync(), entry => entry.Record.RecordId == "draft");
            Assert.Equal(RecordSyncState.Pending, queued.SyncState);
            Assert.Equal(correctedSubmission.RequestJson, queued.PublishedSubmission?.RequestJson);

            async Task ExecuteAsync(string sql)
            {
                var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Password = key, Pooling = false };
                await using var connection = new SqliteConnection(connectionString.ToString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_replay_survives_restart_and_only_one_store_owns_the_upload(bool legacy)
    {
        using var directory = new TemporaryRecordDirectory();
        var photo = Path.Combine(directory.Path, "evidence.jpg");
        await File.WriteAllBytesAsync(photo, [1, 2, 3]);
        var request = new PublishedFormSubmission("inspection", """{"idempotencyKey":"initial","formVersion":3}""",
            [new SubmissionFile("photo", photo, "image/jpeg", "evidence.jpg")], "https://field.example/");
        var draft = new PublishedFormDraft("inspection", "record", "https://field.example/", "{}", "{}");
        var record = CreateRecord("record");
        record.Status = RecordStatus.Submitted;
        IRecordStore initial = legacy
            ? await SqliteRecordStore.OpenEncryptedAsync(directory.Database, "device-test-key")
            : await directory.ReopenAsync();
        await new RecordBook(initial).AddSubmittedAsync(record, request, draft);

        var store = await directory.ReopenAsync();
        var other = await directory.ReopenAsync();
        var first = Assert.Single(await store.LoadAllAsync());
        var second = Assert.Single(await other.LoadAllAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedUploads = 0;
        var response = new TaskCompletionSource<FeatureSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = store.ReplayPublishedAsync(first, async (frozen, token) =>
        {
            entered.SetResult();
            Assert.Equal(request.RequestJson, frozen.RequestJson);
            Assert.Equal(request.Files, frozen.Files);
            return await response.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await other.ReplayPublishedAsync(second, UnexpectedUpload));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.DeleteAsync(record.RecordId));
        response.SetResult(FeatureSyncResult.Fail("Lost response", 503));
        Assert.False(await attempt);

        var restarted = await directory.ReopenAsync();
        var pending = Assert.Single(await restarted.LoadAllAsync());
        Assert.Equal(RecordSyncState.Failed, pending.SyncState);
        Assert.Equal(1, pending.FailedAttempts);
        Assert.NotNull(pending.LastAttemptUtc);
        Assert.False(await restarted.ReplayPublishedAsync(pending, (frozen, _) =>
        {
            Assert.Equal(request.RequestJson, frozen.RequestJson);
            return Task.FromResult(FeatureSyncResult.Fail("Correct the site", 422) with { CanCorrectSubmission = true });
        }));
        Assert.Equal(RecordSyncState.SubmissionRejected, pending.SyncState);
        Assert.True(pending.CanOpenPublishedEditor);
        Assert.False(await restarted.ReplayPublishedAsync(pending, UnexpectedUpload));

        var corrected = request with { RequestJson = """{"idempotencyKey":"corrected","formVersion":3}""" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecordBook(restarted).AddSubmittedAsync(record, request, draft));
        await new RecordBook(restarted).AddSubmittedAsync(record, corrected, draft);
        var finalStore = await directory.ReopenAsync();
        var final = Assert.Single(await finalStore.LoadAllAsync());
        Assert.True(await finalStore.ReplayPublishedAsync(final, (frozen, _) =>
        {
            Assert.Equal(corrected.RequestJson, frozen.RequestJson);
            Assert.Equal(request.Files, frozen.Files);
            return Task.FromResult(FeatureSyncResult.Ok(42));
        }));
        var sent = Assert.Single(await (await directory.ReopenAsync()).LoadAllAsync());
        Assert.Equal(RecordSyncState.Synced, sent.SyncState);
        Assert.Equal("42", sent.RemoteId);
        Assert.False(await finalStore.ReplayPublishedAsync(final, UnexpectedUpload));
        await finalStore.DeleteAsync(record.RecordId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecordBook(finalStore).AddSubmittedAsync(record, corrected, draft));
        Assert.Empty(await finalStore.LoadAllAsync());
        Assert.Equal(0, unexpectedUploads);

        Task<FeatureSyncResult> UnexpectedUpload(PublishedFormSubmission _, CancellationToken __)
        {
            unexpectedUploads++;
            return Task.FromResult(FeatureSyncResult.Fail("Unexpected upload"));
        }
    }

    [Fact]
    public async Task Published_replay_sizes_the_lease_from_relocated_media_without_changing_the_frozen_operation()
    {
        using var directory = new TemporaryRecordDirectory();
        var oldMedia = Path.Combine(directory.Path, "Application", Guid.NewGuid().ToString("D"), "Library", "media");
        var currentMedia = Path.Combine(directory.Path, "Application", Guid.NewGuid().ToString("D"), "Library", "media");
        Directory.CreateDirectory(currentMedia);
        var fileName = Guid.NewGuid().ToString("N") + ".jpg";
        var oldPath = Path.Combine(oldMedia, fileName);
        var currentPath = Path.Combine(currentMedia, fileName);
        await using (var file = File.Create(currentPath))
        {
            file.SetLength(4 * 1024 * 1024);
        }

        var submission = new PublishedFormSubmission("inspection", """{"idempotencyKey":"relocated","formVersion":3}""",
            [new SubmissionFile("photo", oldPath, "image/jpeg", fileName)], "https://field.example/")
            { ReplayPrincipal = "test-principal" };
        var record = CreateRecord("record");
        record.Status = RecordStatus.Submitted;
        var store = await directory.ReopenAsync();
        await new RecordBook(store).AddSubmittedAsync(record, submission);
        var pending = Assert.Single(await store.LoadAllAsync());

        PublishedFormSubmission? sent = null;
        DateTimeOffset? leaseUntil = null;
        var claimStarted = DateTimeOffset.UtcNow;
        Assert.False(await store.ReplayPublishedAsync(pending, async (replay, token) =>
        {
            sent = replay;
            var settings = new SqliteConnectionStringBuilder
                { DataSource = directory.Database, Password = "device-test-key", Pooling = false };
            await using var connection = new SqliteConnection(settings.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT lease_until_utc FROM honua_local_operations WHERE document_id = 'record';";
            if (await command.ExecuteScalarAsync(token) is string until)
            {
                leaseUntil = DateTimeOffset.Parse(until, System.Globalization.CultureInfo.InvariantCulture);
            }

            return FeatureSyncResult.Fail("Lost response", 503);
        }, mediaDirectory: currentMedia));

        Assert.NotNull(sent);
        Assert.Equal(currentPath, Assert.Single(sent.Files).LocalPath);
        Assert.Equal(submission.RequestJson, sent.RequestJson);
        Assert.Equal(submission.ReplayPrincipal, sent.ReplayPrincipal);
        Assert.Equal(submission.ServerBaseUrl, sent.ServerBaseUrl);
        // Four MiB at 8 KiB/s takes 512 seconds, plus the deadline and lease margins.
        Assert.True(leaseUntil >= claimStarted.AddSeconds(632));
        var durable = Assert.Single(await (await directory.ReopenAsync()).LoadAllAsync());
        Assert.Equal(RecordSyncState.Failed, durable.SyncState);
        Assert.NotNull(durable.PublishedSubmission);
        Assert.Equal(oldPath, Assert.Single(durable.PublishedSubmission.Files).LocalPath);
        Assert.Equal(submission.RequestJson, durable.PublishedSubmission.RequestJson);
    }

    [Fact]
    public async Task Legacy_conflict_retains_local_state_without_fabricating_server_comparison()
    {
        using var directory = new TemporaryRecordDirectory();
        const string key = "device-test-key";
        var legacy = await SqliteRecordStore.OpenEncryptedAsync(directory.Database, key);
        var local = new FieldRecord { RecordId = "legacy-conflict", FormId = "inspection", Status = RecordStatus.Submitted };
        local.Values["name"] = "Mine";
        var server = CreateRecord("legacy-conflict");
        server.Values["name"] = "Theirs";
        var entry = new FieldRecordEntry(local);
        entry.MarkPending();
        entry.MarkSynced("11");
        entry.SetVersion(17);
        entry.MarkConflicted(new RecordConflict(local, server, [new FieldConflict("name", "Name", "Mine", "Theirs")]));
        await legacy.SaveAsync(entry);

        var store = await MobileRecordStore.OpenEncryptedAsync(directory.Database, key);
        var restored = Assert.Single(await store.LoadAllAsync());
        Assert.True(restored.IsConflicted);
        Assert.Equal("11", restored.RemoteId);
        Assert.Equal(17, restored.Version);
        Assert.Equal("Mine", restored.Record.Values["name"]?.ToString());
        Assert.Null(restored.Conflict);
    }

    private sealed class TemporaryRecordDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
        public string Path => _directory.FullName;
        public string Database => System.IO.Path.Combine(Path, "records.db");
        public Task<MobileRecordStore> ReopenAsync() => MobileRecordStore.OpenEncryptedAsync(Database, "device-test-key");
        public void Dispose() => _directory.Delete(recursive: true);
    }

    private static FieldRecord CreateRecord(string id) => new() { RecordId = id, FormId = "inspection" };
}
