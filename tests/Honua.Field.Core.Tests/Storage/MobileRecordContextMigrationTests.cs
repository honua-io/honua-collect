// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Assignments;
using Honua.Field.Core.Field.Related;
using Honua.Field.Core.History;
using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;
using Honua.Field.Core.Sync;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Tests.Storage;

public sealed class MobileRecordContextMigrationTests
{
    [Fact]
    public async Task Assigned_record_keeps_its_linked_child_and_post_sync_edit_history_after_upgrade()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "records.db");
            const string key = "device-test-key";
            const string worker = "inspector-1";
            var legacy = await SqliteRecordStore.OpenEncryptedAsync(path, key);
            var parent = new FieldRecordEntry(new FieldRecord
            {
                RecordId = Guid.NewGuid().ToString("N"),
                FormId = "hydrant-inspection",
                AssignedUserId = worker,
                Status = RecordStatus.Submitted,
            });
            var child = new FieldRecord { RecordId = Guid.NewGuid().ToString("N"), FormId = "deficiency" };
            parent.MarkSynced("42");
            parent.MarkEditedAfterSync();
            parent.Record.Values["condition"] = "leaking";
            var originalValues = new Dictionary<string, object?> { ["condition"] = "dry" };
            var log = new RecordEditLog(new SqliteRecordHistoryStore(path, key));
            await log.RecordEditAsync(parent, originalValues, worker);
            await legacy.SaveAsync(parent);
            await legacy.SaveAsync(new FieldRecordEntry(child));

            var assignment = new FieldAssignment
            {
                AssignmentId = "hydrant-42",
                FormId = parent.Record.FormId,
                AssignedToUserId = worker,
                Title = "Inspect hydrant",
            };
            assignment.Start(parent.Record.RecordId);
            await new SqliteAssignmentStore(path, key).SaveAsync(assignment);
            await new SqliteRelatedRecordStore(path, key).LinkAsync(parent.Record.RecordId, "deficiencies",
                new FieldRecordLinkValue { RecordId = child.RecordId, FormId = child.FormId }, RecordLinkBehavior.Restrict);

            await MobileRecordStore.OpenEncryptedAsync(path, key);
            var restarted = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var records = (await restarted.LoadAllAsync()).ToDictionary(entry => entry.Record.RecordId);
            var assignments = new SqliteAssignmentStore(path, key);
            var work = Assert.Single(await assignments.LoadForUserAsync(worker));
            Assert.Equal(assignment.AssignmentId, work.AssignmentId);
            Assert.Equal(AssignmentStatus.InProgress, work.Status);
            Assert.Empty(await assignments.LoadForUserAsync("inspector-2"));
            Assert.Equal(parent.Record.RecordId, work.RecordId);
            var restored = records[work.RecordId!];
            Assert.Equal(worker, restored.Record.AssignedUserId);
            Assert.True(restored.IsServerUpdate);
            Assert.Equal("42", restored.RemoteId);
            Assert.Equal("leaking", restored.Record.Values["condition"]?.ToString());

            var links = new SqliteRelatedRecordStore(path, key);
            await Assert.ThrowsAsync<RelatedRecordIntegrityException>(() => links.DeleteParentAsync(restored.Record.RecordId));
            var link = Assert.Single(await links.ListAsync(restored.Record.RecordId, "deficiencies"));
            Assert.Equal(child.RecordId, link.RecordId);
            Assert.Equal(link.FormId, records[link.RecordId].Record.FormId);
            Assert.Equal(2, records.Count);

            var history = new SqliteRecordHistoryStore(path, key);
            var edit = Assert.Single(await history.GetHistoryAsync(restored.Record.RecordId));
            Assert.Equal(worker, edit.EditorUserId);
            Assert.True(edit.AfterSync);
            Assert.Equal(0, edit.Sequence);
            Assert.Equal(new FieldChange("condition", "dry", "leaking"), Assert.Single(edit.Changes));
            Assert.Equal(1, restored.Version);
            Assert.Equal(restored.Version, await history.GetNextSequenceAsync(restored.Record.RecordId));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Ordinary_photo_checkpoints_survive_store_migration_and_reopen()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "records.db");
            const string key = "device-test-key";
            var entry = new FieldRecordEntry(new FieldRecord
            {
                RecordId = "inspection-42", FormId = "inspection", Status = RecordStatus.Submitted,
            });
            entry.MarkSynced("42");
            var photo = new OrdinaryFeaturePhoto(Guid.NewGuid(), "capture.jpg", "image/jpeg",
                "capture.jpg", new string('A', 64), new GeoServicesTarget("https://example.test", "inspections", 0));
            entry.AddOrdinaryPhoto(photo);

            var legacy = await SqliteRecordStore.OpenEncryptedAsync(path, key);
            await legacy.SaveAsync(entry);
            var mobile = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var migrated = Assert.Single(await mobile.LoadAllAsync());
            var stale = Assert.Single(await mobile.LoadAllAsync());
            Assert.Equal(photo, Assert.Single(migrated.OrdinaryPhotos));

            var verifying = photo with { State = OrdinaryPhotoState.NeedsVerification };
            await migrated.UpdateOrdinaryPhotoAsync(verifying, current => mobile.SaveAsync(current));
            await Assert.ThrowsAsync<InvalidOperationException>(() => mobile.SaveAsync(stale));

            var reopened = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var restored = Assert.Single(await reopened.LoadAllAsync());
            Assert.Equal("42", restored.RemoteId);
            Assert.Equal(verifying, Assert.Single(restored.OrdinaryPhotos));
            Assert.Equal(RecordBox.Outbox, restored.Box);

            var receipt = verifying with { State = OrdinaryPhotoState.Synced, ServerAttachmentId = 17 };
            await restored.UpdateOrdinaryPhotoAsync(receipt, current => reopened.SaveAsync(current));
            var restarted = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var completed = Assert.Single(await restarted.LoadAllAsync());
            Assert.Equal(receipt, Assert.Single(completed.OrdinaryPhotos));
            Assert.Equal(RecordBox.Sent, completed.Box);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(RecordSyncState.Pending)]
    [InlineData(RecordSyncState.Uploading)]
    [InlineData(RecordSyncState.Failed)]
    [InlineData(RecordSyncState.Conflicted)]
    public async Task Reopened_edit_keeps_the_server_feature_it_must_update(RecordSyncState state)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "records.db");
            const string key = "device-test-key";
            var syncedAt = DateTimeOffset.UnixEpoch.AddDays(20_000);
            var record = new FieldRecord { RecordId = "inspection-42", FormId = "inspection", Status = RecordStatus.Submitted };
            var entry = new FieldRecordEntry(record);
            entry.MarkSynced("42", syncedAt);
            entry.MarkEditedAfterSync();
            record.Values["condition"] = "leaking";
            var form = new FormDefinition
            {
                FormId = record.FormId,
                Name = "Inspection",
                Sections = [new FormSection { SectionId = "s", Label = "Inspection", Fields =
                    [new FormField { FieldId = "condition", Label = "Condition", Type = FormFieldType.Text }] }],
            };
            var server = new FieldRecord { RecordId = record.RecordId, FormId = record.FormId, Status = record.Status };
            server.Values["condition"] = "dry";
            switch (state)
            {
                case RecordSyncState.Pending:
                    entry.MarkPending();
                    break;
                case RecordSyncState.Uploading:
                    entry.MarkUploading();
                    break;
                case RecordSyncState.Failed:
                    entry.MarkFailed("Disconnected");
                    break;
                case RecordSyncState.Conflicted:
                    entry.MarkConflicted(RecordConflictDetector.Detect(form, record, server));
                    break;
            }

            var legacy = await SqliteRecordStore.OpenEncryptedAsync(path, key);
            await legacy.SaveAsync(entry);
            var migrated = await MobileRecordStore.OpenEncryptedAsync(path, key);
            AssertStoredEdit(Assert.Single(await migrated.LoadAllAsync()));

            await migrated.SaveAsync(entry);
            var restarted = await MobileRecordStore.OpenEncryptedAsync(path, key);
            var reopened = Assert.Single(await restarted.LoadAllAsync());
            AssertStoredEdit(reopened);
            if (state == RecordSyncState.Conflicted)
            {
                var conflict = RecordConflictDetector.Detect(form, reopened.Record, server);
                reopened.ApplyResolution(conflict.ResolveAll(ConflictResolution.KeepLocal));
                Assert.True(reopened.IsServerUpdate);
                Assert.Equal("leaking", reopened.Record.Values["condition"]?.ToString());
            }

            void AssertStoredEdit(FieldRecordEntry stored)
            {
                Assert.Equal(state, stored.SyncState);
                Assert.Equal("42", stored.RemoteId);
                Assert.Equal(syncedAt, stored.LastSyncedUtc);
                Assert.Equal(entry.FailedAttempts, stored.FailedAttempts);
                Assert.Equal(entry.LastError, stored.LastError);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
