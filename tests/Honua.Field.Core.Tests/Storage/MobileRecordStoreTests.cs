// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;
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

    private static FieldRecord CreateRecord(string id) => new() { RecordId = id, FormId = "inspection" };
}
