// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Assignments;
using Honua.Field.Core.Field.Related;
using Honua.Field.Core.History;
using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;
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
                AssignmentId = "hydrant-42", FormId = parent.Record.FormId, AssignedToUserId = worker, Title = "Inspect hydrant",
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
}
