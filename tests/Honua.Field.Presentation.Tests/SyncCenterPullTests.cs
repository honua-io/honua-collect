// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Field.Core.Sync;
using Honua.Field.Core.Storage;
using Honua.Field.Presentation.Sync;
using Honua.Sdk.Field.Forms;
using PublishedFormSubmission = Honua.Field.Core.Forms.PublishedFormSubmission;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Field.Presentation.Tests;

public class SyncCenterPullTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "f",
        Name = "f",
        Sections =
        [
            new FormSection
            {
                SectionId = "s",
                Label = "s",
                Fields = [new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text }],
            },
        ],
    };

    private static FieldRecordEntry SyncedEntry(string recordId, string remoteId, string name)
    {
        var record = new FieldRecord { RecordId = recordId, FormId = "f", Status = RecordStatus.Submitted };
        record.Values["name"] = name;
        var entry = new FieldRecordEntry(record);
        entry.MarkPending();
        entry.MarkSynced(remoteId: remoteId);
        return entry;
    }

    private static PulledRecord Server(long objectId, string name)
    {
        var r = new FieldRecord { RecordId = objectId.ToString(), FormId = string.Empty, Status = RecordStatus.Submitted };
        r.Values["name"] = name;
        return new PulledRecord(objectId, r);
    }

    private static FeaturePuller Puller(params PulledRecord[] records)
        => _ => Task.FromResult(new FeatureQueryResult(true, records, null));

    [Fact]
    public async Task Pull_does_not_conflict_with_a_published_submission_with_the_same_feature_id()
    {
        var record = new FieldRecord { RecordId = "published", FormId = "f", Status = RecordStatus.Submitted };
        var entry = new FieldRecordEntry(record) { PublishedSubmission = new PublishedFormSubmission("f", "{}", []) };
        entry.MarkPending();
        entry.MarkSynced("11");
        var vm = new SyncCenterViewModel([entry], (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")),
            Puller(Server(11, "Different target")), Form());

        await vm.PullAsync();

        Assert.Equal(11, Assert.Single(vm.NewFromServer).ObjectId);
        Assert.Empty(vm.Conflicts);
        Assert.Equal(RecordBox.Sent, entry.Box);
    }

    [Fact]
    public async Task PullAsync_builds_conflict_list_for_diverging_features()
    {
        // local rec-1 synced as object id 11, edited to "Mine"; server still says "Theirs".
        var entries = new[] { SyncedEntry("rec-1", "11", "Mine") };
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")),
            Puller(Server(11, "Theirs"), Server(99, "Brand New")), Form());

        var merge = await vm.PullAsync();

        Assert.NotNull(merge);
        Assert.Single(vm.Conflicts);
        var review = vm.Conflicts[0];
        Assert.True(review.HasConflicts);
        var field = Assert.Single(review.Conflicts);
        Assert.Equal("Mine", field.LocalText);
        Assert.Equal("Theirs", field.ServerText);

        // The unmatched server feature surfaces as new-from-server.
        Assert.Single(vm.NewFromServer);
        Assert.Equal(99, vm.NewFromServer[0].ObjectId);
    }

    [Fact]
    public async Task PullAsync_moves_the_diverging_local_entry_into_the_conflicts_box()
    {
        var entries = new[] { SyncedEntry("rec-1", "11", "Mine") };
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")),
            Puller(Server(11, "Theirs")), Form());

        await vm.PullAsync();

        // The owning local entry is now conflicted, out of the Outbox, and the
        // sync center surfaces the count.
        Assert.Equal(RecordBox.Conflicts, entries[0].Box);
        Assert.True(entries[0].IsConflicted);
        Assert.Equal(1, vm.Summary.Conflicts);
        Assert.Contains("Conflicts 1", vm.Header);

        // The review is bound to that entry, so applying it re-queues the record.
        var review = vm.Conflicts[0];
        Assert.True(review.CanApply);
        review.Conflicts[0].Resolution = ConflictResolution.KeepLocal;
        review.ApplyResolution();

        Assert.False(entries[0].IsConflicted);
        Assert.Equal(RecordBox.Outbox, entries[0].Box);
        Assert.Equal("Mine", entries[0].Record.Values["name"]);
        Assert.True(review.IsResolved);
        Assert.False(review.ApplyResolutionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pull_warns_when_conflict_cannot_be_saved_and_can_retry_the_save()
    {
        var entry = SyncedEntry("rec-1", "11", "Mine");
        var attempts = 0;
        var vm = new SyncCenterViewModel([entry], (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")),
            Puller(Server(11, "Theirs")), Form(), persist: _ => ++attempts == 1
                ? Task.FromException(new IOException("Local store unavailable"))
                : Task.CompletedTask);

        await vm.PullAsync();

        Assert.True(vm.HasUnsavedPullConflicts);
        Assert.True(vm.HasReviewableConflicts);
        Assert.Equal(RecordBox.Conflicts, entry.Box);

        await vm.PullAsync();

        Assert.False(vm.HasUnsavedPullConflicts);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task PullAsync_returns_null_and_no_conflicts_on_query_failure()
    {
        var entries = new[] { SyncedEntry("rec-1", "11", "Mine") };
        FeaturePuller failing = _ => Task.FromResult(FeatureQueryResult.Fail("boom", 500));
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")), failing, Form());

        var merge = await vm.PullAsync();

        Assert.Null(merge);
        Assert.Empty(vm.Conflicts);
    }

    [Fact]
    public async Task PullAsync_no_conflicts_when_server_matches_local()
    {
        var entries = new[] { SyncedEntry("rec-1", "11", "Same") };
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")),
            Puller(Server(11, "Same")), Form());

        var merge = await vm.PullAsync();

        Assert.NotNull(merge);
        Assert.False(merge!.HasConflicts);
        Assert.Empty(vm.Conflicts);
        Assert.Empty(vm.NewFromServer);
    }

    [Fact]
    public async Task Resolving_a_conflict_persists_the_merge_and_requeues_for_upload()
    {
        // Regression for #98: the review screen must APPLY and PERSIST the user's
        // resolution, not silently discard it. ApplyResolutionAsync is the call the
        // ConflictReviewPage now makes; it must mutate the entry AND hand it to the
        // durable persister so the merge survives a restart and re-uploads.
        var entries = new[] { SyncedEntry("rec-1", "11", "Mine") };
        var vm = new SyncCenterViewModel(entries, (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")),
            Puller(Server(11, "Theirs")), Form());

        await vm.PullAsync();
        var review = Assert.Single(vm.Conflicts);
        review.Conflicts[0].Resolution = ConflictResolution.KeepLocal;

        var persisted = new List<FieldRecordEntry>();
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolving = review.ApplyResolutionAsync(entry =>
        {
            persisted.Add(entry);
            return write.Task;
        });

        Assert.True(entries[0].IsConflicted);
        Assert.False(review.CanApply);
        Assert.False(review.IsResolved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.ApplyResolutionAsync(null));
        write.SetResult();
        await resolving;

        // The chosen merge is applied to the local entry...
        Assert.Equal("Mine", entries[0].Record.Values["name"]);
        Assert.False(entries[0].IsConflicted);
        Assert.Null(entries[0].Conflict);
        // ...re-queued as an UPDATE (it already exists on the server)...
        Assert.Equal(RecordSyncState.PendingUpdate, entries[0].SyncState);
        Assert.Equal(RecordBox.Outbox, entries[0].Box);
        // ...and durably persisted exactly once (no silent discard).
        var saved = Assert.Single(persisted);
        Assert.NotSame(entries[0], saved);
        Assert.Equal(RecordSyncState.PendingUpdate, saved.SyncState);
        Assert.Null(saved.Conflict);
        Assert.True(review.IsResolved);
    }

    [Fact]
    public async Task Persisted_conflict_can_be_reviewed_and_resolved_after_offline_restart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "records.db");
            var store = await MobileRecordStore.OpenEncryptedAsync(path, "conflict-test-key");
            var entry = SyncedEntry("rec-1", "11", "Mine");
            entry.SetVersion(17);
            await store.SaveAsync(entry);
            var server = Server(11, "Theirs");
            server.Record.Values["server_only"] = "retained";
            RecordUploader noUpload = (_, _) => Task.FromResult(FeatureSyncResult.Fail("offline"));
            var online = new SyncCenterViewModel([entry], noUpload, Puller(server), Form(), persist: e => store.SaveAsync(e));

            await online.PullAsync();

            var reopened = await MobileRecordStore.OpenEncryptedAsync(path, "conflict-test-key");
            var offline = new SyncCenterViewModel(await reopened.LoadAllAsync(), noUpload);
            Assert.False(offline.CanPull);
            Assert.True(offline.HasReviewableConflicts);
            Assert.Equal(1, offline.Summary.Conflicts);
            var review = Assert.Single(offline.Conflicts);
            var field = Assert.Single(review.Conflicts);
            Assert.Equal("Mine", field.LocalText);
            Assert.Equal("Theirs", field.ServerText);
            field.Resolution = ConflictResolution.KeepServer;

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Password = "conflict-test-key",
                Pooling = false,
            };
            await using (var database = new SqliteConnection(connectionString.ToString()))
            {
                await database.OpenAsync();
                await using var command = database.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fail_conflict_write BEFORE UPDATE ON honua_local_documents
                    BEGIN
                        SELECT RAISE(ABORT, 'test interrupted resolution');
                    END;
                    """;
                await command.ExecuteNonQueryAsync();

                await Assert.ThrowsAsync<SqliteException>(() => review.ApplyResolutionAsync(e => reopened.SaveAsync(e)));

                Assert.True(review.CanApply);
                Assert.False(review.IsResolved);
                Assert.Equal(ConflictResolution.KeepServer, field.Resolution);
                var retained = Assert.Single(await reopened.LoadAllAsync());
                Assert.True(retained.IsConflicted);
                Assert.Equal("Mine", retained.Record.Values["name"]?.ToString());
                Assert.NotNull(retained.Conflict);

                command.CommandText = "DROP TRIGGER fail_conflict_write;";
                await command.ExecuteNonQueryAsync();
            }

            await review.ApplyResolutionAsync(e => reopened.SaveAsync(e));
            Assert.True(review.IsResolved);

            var resolvedStore = await MobileRecordStore.OpenEncryptedAsync(path, "conflict-test-key");
            var resolved = Assert.Single(await resolvedStore.LoadAllAsync());
            Assert.Equal(RecordSyncState.PendingUpdate, resolved.SyncState);
            Assert.Equal("11", resolved.RemoteId);
            Assert.Equal(17, resolved.Version);
            Assert.Equal("Theirs", resolved.Record.Values["name"]?.ToString());
            Assert.Equal("retained", resolved.Record.Values["server_only"]?.ToString());
            Assert.Null(resolved.Conflict);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Pull_path_is_disabled_when_not_configured()
    {
        var vm = new SyncCenterViewModel(
            [SyncedEntry("rec-1", "11", "x")],
            (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")));

        Assert.False(vm.CanPull);
        Assert.False(vm.PullCommand.CanExecute(null));
    }

    private static FieldRecordEntry PendingEntry(string recordId, string name)
    {
        var record = new FieldRecord { RecordId = recordId, FormId = "f", Status = RecordStatus.Submitted };
        record.Values["name"] = name;
        var entry = new FieldRecordEntry(record);
        entry.MarkPending();
        return entry;
    }

    [Fact]
    public async Task SyncAsync_uploads_pending_records_and_marks_them_synced()
    {
        var entries = new[] { PendingEntry("a", "A"), PendingEntry("b", "B") };
        var ids = new Dictionary<string, long> { ["a"] = 101, ["b"] = 102 };
        var vm = new SyncCenterViewModel(entries, (e, _) => Task.FromResult(FeatureSyncResult.Ok(ids[e.Record.RecordId])));
        Assert.True(vm.SyncCommand.CanExecute(null));

        var synced = await vm.SyncAsync();

        Assert.Equal(2, synced);
        Assert.Equal("101", entries[0].RemoteId);
        Assert.Equal("102", entries[1].RemoteId);
        Assert.Empty(vm.Pending);
        Assert.False(vm.SyncCommand.CanExecute(null)); // nothing left to do
    }

    [Fact]
    public async Task SyncAsync_surfaces_the_server_error_message_and_code_verbatim()
    {
        // AUD-258: a failed upload must thread the server's real message/code through
        // to the entry rather than collapsing to a generic "rejected" sentinel, so an
        // operator can tell a permissions/token failure from a transient one.
        var entries = new[] { PendingEntry("a", "A") };
        var vm = new SyncCenterViewModel(
            entries, (_, _) => Task.FromResult(FeatureSyncResult.Fail("Token expired", 498)));

        var synced = await vm.SyncAsync();

        Assert.Equal(0, synced);
        Assert.Equal("Token expired (code 498)", entries[0].LastError);
        Assert.Single(vm.Pending); // a failed record remains in the outbox
    }

    [Fact]
    public async Task SyncAsync_marks_failed_and_continues_on_a_thrown_error()
    {
        var entries = new[] { PendingEntry("a", "A"), PendingEntry("b", "B") };
        var vm = new SyncCenterViewModel(entries, (e, _) =>
            e.Record.RecordId == "a"
                ? throw new InvalidOperationException("network down")
                : Task.FromResult(FeatureSyncResult.Ok(202)));

        var synced = await vm.SyncAsync();

        Assert.Equal(1, synced); // "b" still succeeds after "a" failed
        Assert.Equal("network down", entries[0].LastError);
        Assert.Equal("202", entries[1].RemoteId);
    }

    [Fact]
    public async Task SyncAsync_lets_cancellation_propagate()
    {
        var entries = new[] { PendingEntry("a", "A") };
        var vm = new SyncCenterViewModel(entries, (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(FeatureSyncResult.Ok(1));
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.SyncAsync(cts.Token));
    }

    [Fact]
    public async Task SyncAsync_is_a_no_op_while_already_syncing()
    {
        var gate = new TaskCompletionSource<FeatureSyncResult>();
        var entries = new[] { PendingEntry("a", "A") };
        var vm = new SyncCenterViewModel(entries, (_, _) => gate.Task);

        var first = vm.SyncAsync();           // enters and parks on the uploader
        var second = await vm.SyncAsync();    // re-entrancy guard returns immediately

        Assert.Equal(0, second);
        gate.SetResult(FeatureSyncResult.Ok(1));
        Assert.Equal(1, await first);
    }
}
