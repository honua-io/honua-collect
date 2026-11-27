// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Field.Core.Sync;
using Honua.Field.Presentation.Mvvm;
using Honua.Field.Presentation.Records;
using Honua.Field.Presentation.Sync;
using Honua.Sdk.Field.Records;
using Microsoft.Extensions.Logging;

namespace Honua.Field.Presentation.Tests;

public class SyncCenterCommandTests
{
    private static FieldRecordEntry PendingEntry(string recordId)
    {
        var record = new FieldRecord { RecordId = recordId, FormId = "f", Status = RecordStatus.Submitted };
        record.Values["name"] = "v";
        var entry = new FieldRecordEntry(record);
        entry.MarkPending();
        return entry;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_photo_outbox_waits_for_original_target_and_retries_without_resending_feature(bool batch)
    {
        var entry = PendingEntry("inspection");
        var originalTarget = new GeoServicesTarget("https://field.example/", "inspections", 1);
        var currentTarget = originalTarget with { LayerId = 2 };
        var photo = new OrdinaryFeaturePhoto(Guid.NewGuid(), "/photo.jpg", "image/jpeg", "photo.jpg", "hash", originalTarget);
        entry.AddOrdinaryPhoto(photo);
        var uploads = 0;
        var photoAttempts = 0;
        var saves = 0;
        BatchRecordUploader? batchUploader = batch ? (rows, _) =>
        {
            Assert.Same(entry, Assert.Single(rows));
            uploads++;
            return Task.FromResult<IReadOnlyList<FeatureSyncResult>>([FeatureSyncResult.Ok(17)]);
        } : null;
        var vm = new SyncCenterViewModel([entry], (row, _) =>
        {
            Assert.False(batch);
            Assert.Same(entry, row);
            uploads++;
            return Task.FromResult(FeatureSyncResult.Ok(17));
        }, puller: null, form: null, persist: _ =>
        {
            saves++;
            return Task.CompletedTask;
        }, batchUploader: batchUploader,
            replayOrdinaryPhotos: row =>
            {
                Assert.Equal("17", row.RemoteId);
                if (++photoAttempts == 1)
                {
                    return Task.FromResult(false);
                }

                row.UpdateOrdinaryPhoto(photo with { State = OrdinaryPhotoState.Synced, ServerAttachmentId = 2 });
                return Task.FromResult(true);
            },
            canDispatchOrdinaryRecord: row => Assert.Single(row.OrdinaryPhotos).Target == currentTarget);

        Assert.Equal(0, await vm.SyncAsync());
        Assert.Equal(0, uploads);
        Assert.Equal(0, saves);
        Assert.Equal(0, photoAttempts);
        Assert.True(entry.IsPendingUpload);

        currentTarget = originalTarget;
        Assert.Equal(0, await vm.SyncAsync());
        Assert.Equal(1, uploads);
        Assert.Equal(1, photoAttempts);
        Assert.False(entry.IsPendingUpload);
        Assert.Equal(RecordBox.Outbox, entry.Box);
        Assert.Contains("Photos still need sync", new RecordRowViewModel(entry).StatusText);
        Assert.True(vm.SyncCommand.CanExecute(null));

        Assert.Equal(1, await vm.SyncAsync());
        Assert.Equal(1, uploads);
        Assert.Equal(2, photoAttempts);
        Assert.Equal(RecordBox.Sent, entry.Box);
        Assert.False(vm.SyncCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_queue_replays_the_same_request_after_a_503_and_parks_only_a_definitive_rejection(bool hasSnapshot)
    {
        var request = new PublishedFormSubmission("f", """{"idempotencyKey":"original"}""", []);
        var record = new FieldRecord { RecordId = "inspection", FormId = "f", Status = RecordStatus.Submitted };
        var entry = new FieldRecordEntry(record)
        {
            PublishedSubmission = request,
            PublishedDraft = hasSnapshot ? new PublishedFormDraft("f", "inspection", "https://field.example/", "{}", "{}") : null,
        };
        entry.MarkPending();
        var requests = new List<PublishedFormSubmission>();
        var vm = new SyncCenterViewModel([entry], (_, _) => throw new InvalidOperationException("Legacy uploader used."),
            puller: null, form: null,
            replayPublished: (pending, _) =>
            {
                requests.Add(pending.PublishedSubmission!);
                if (requests.Count == 1)
                {
                    pending.MarkFailed("HTTP 503");
                }
                else
                {
                    pending.MarkSubmissionRejected("Correct the site name.");
                }

                return Task.FromResult(false);
            });

        await vm.SyncAsync();
        Assert.Equal(RecordSyncState.Failed, entry.SyncState);
        Assert.False(entry.CanOpenPublishedEditor);
        Assert.True(entry.IsPendingUpload);
        Assert.True(vm.SyncCommand.CanExecute(null));
        Assert.False(vm.HasRejectedSubmissions);

        await vm.SyncAsync();
        Assert.Equal(RecordSyncState.SubmissionRejected, entry.SyncState);
        Assert.Equal(hasSnapshot, entry.CanOpenPublishedEditor);
        Assert.False(entry.IsPendingUpload);
        Assert.Equal(1, vm.Summary.Failed);
        Assert.True(vm.HasRejectedSubmissions);
        Assert.False(vm.SyncCommand.CanExecute(null));

        await vm.SyncAsync();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, sent => Assert.Same(request, sent));
    }

    [Fact]
    public async Task SyncCommand_is_disabled_the_moment_a_sync_starts()
    {
        var gate = new TaskCompletionSource<FeatureSyncResult>();
        var entries = new[] { PendingEntry("r1") };
        var vm = new SyncCenterViewModel(entries, (_, _) => gate.Task);

        Assert.True(vm.SyncCommand.CanExecute(null)); // pending work, not syncing

        var canExecuteChanges = 0;
        vm.SyncCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        var run = vm.SyncAsync();

        // The sync is in flight (the uploader is awaiting the gate): the command
        // must already report disabled, and CanExecuteChanged must have fired so
        // a bound button is greyed out immediately rather than staying tappable.
        Assert.True(vm.IsSyncing);
        Assert.False(vm.SyncCommand.CanExecute(null));
        Assert.True(canExecuteChanges >= 1);

        gate.SetResult(FeatureSyncResult.Ok(1));
        await run;

        Assert.False(vm.IsSyncing);
    }

    [Fact]
    public async Task A_successful_sync_persists_the_synced_entry_state()
    {
        // The upload mutates the entry to Synced; that state must be persisted so a
        // restart does not reload it as Pending and re-upload (duplicating the
        // server feature). AUD-214.
        var persisted = new List<(string Id, RecordSyncState State)>();
        var entry = PendingEntry("r1");
        var vm = new SyncCenterViewModel(
            new[] { entry },
            (_, _) => Task.FromResult(FeatureSyncResult.Ok(1)),
            puller: null,
            form: null,
            persist: e => { persisted.Add((e.Record.RecordId, e.SyncState)); return Task.CompletedTask; });

        await vm.SyncAsync();

        Assert.Equal(RecordSyncState.Synced, entry.SyncState);
        Assert.Equal(
            new[] { ("r1", RecordSyncState.AddNeedsVerification), ("r1", RecordSyncState.Synced) },
            persisted);
    }

    [Fact]
    public async Task A_persistence_failure_does_not_downgrade_a_synced_record()
    {
        // If the local durable write fails after the server already accepted the
        // upload, the in-memory Synced state must stand: flipping it back to a
        // re-uploadable state would risk a duplicate on the next pass.
        var entry = PendingEntry("r1");
        var vm = new SyncCenterViewModel(
            new[] { entry },
            (_, _) => Task.FromResult(FeatureSyncResult.Ok(1)),
            puller: null,
            form: null,
            persist: e => e.SyncState == RecordSyncState.Synced
                ? throw new InvalidOperationException("disk full")
                : Task.CompletedTask);

        var synced = await vm.SyncAsync();

        Assert.Equal(1, synced);
        Assert.Equal(RecordSyncState.Synced, entry.SyncState);
    }

    [Fact]
    public async Task SyncAsync_batches_adds_into_chunked_round_trips()
    {
        // AUD-256: with a batch uploader, new-record adds go up in chunked applyEdits
        // round-trips rather than one HTTP request per record.
        var entries = new[] { PendingEntry("r1"), PendingEntry("r2"), PendingEntry("r3") };
        var batchSizes = new List<int>();
        BatchRecordUploader batch = (es, _) =>
        {
            batchSizes.Add(es.Count);
            IReadOnlyList<FeatureSyncResult> results =
                es.Select((_, i) => FeatureSyncResult.Ok(100 + i)).ToList();
            return Task.FromResult(results);
        };
        var vm = new SyncCenterViewModel(
            entries,
            (_, _) => Task.FromResult(FeatureSyncResult.Fail("per-record uploader must not run for adds")),
            puller: null, form: null, persist: null, batchUploader: batch, batchSize: 2);

        var synced = await vm.SyncAsync();

        Assert.Equal(3, synced);
        Assert.Equal([2, 1], batchSizes); // chunked at 2 -> [r1,r2] then [r3]
        Assert.All(entries, e => Assert.Equal(RecordSyncState.Synced, e.SyncState));
    }

    [Fact]
    public async Task SyncAsync_batch_holds_an_unreported_tail_record_for_verification()
    {
        var entries = new[] { PendingEntry("r1"), PendingEntry("r2") };
        BatchRecordUploader batch = (_, _) =>
            Task.FromResult<IReadOnlyList<FeatureSyncResult>>([FeatureSyncResult.Ok(1)]); // short by one
        var vm = new SyncCenterViewModel(
            entries,
            (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")),
            puller: null, form: null, persist: null, batchUploader: batch);

        var synced = await vm.SyncAsync();

        Assert.Equal(1, synced);
        Assert.Equal(RecordSyncState.Synced, entries[0].SyncState);
        Assert.Equal(RecordSyncState.AddNeedsVerification, entries[1].SyncState);
    }

    [Fact]
    public async Task SyncAsync_persists_acknowledged_batch_member_and_holds_unresolved_member()
    {
        var entries = new[] { PendingEntry("r1"), PendingEntry("r2") };
        var sent = new List<string[]>();
        var persisted = new List<(string RecordId, RecordSyncState State, string? RemoteId)>();
        BatchRecordUploader batch = (members, _) =>
        {
            sent.Add(members.Select(member => member.Record.RecordId).ToArray());
            IReadOnlyList<FeatureSyncResult> results = sent.Count == 1
                ? [FeatureSyncResult.Ok(17), FeatureSyncResult.Fail("Layer reported a general failure")]
                : [FeatureSyncResult.Ok(18)];
            return Task.FromResult(results);
        };
        var vm = new SyncCenterViewModel(
            entries,
            (_, _) => Task.FromResult(FeatureSyncResult.Fail("unused")),
            puller: null, form: null,
            persist: entry =>
            {
                persisted.Add((entry.Record.RecordId, entry.SyncState, entry.RemoteId));
                return Task.CompletedTask;
            },
            batchUploader: batch);

        Assert.Equal(1, await vm.SyncAsync());
        Assert.Contains(("r1", RecordSyncState.Synced, "17"), persisted);
        Assert.Equal(("r2", RecordSyncState.AddNeedsVerification, null),
            Assert.Single(persisted, change => change.RecordId == "r2"));

        Assert.Equal(0, await vm.SyncAsync());
        Assert.Equal(["r1", "r2"], Assert.Single(sent));
        Assert.True(entries[1].RequiresServerVerification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_add_stays_pending_until_connected_and_dispatch_hold_is_saved(bool batch)
    {
        var entry = PendingEntry("r1");
        var connected = false;
        var saveAttempts = 0;
        var sends = 0;
        var vm = new SyncCenterViewModel(
            [entry], (_, _) =>
            {
                sends++;
                return Task.FromResult(FeatureSyncResult.Ok(17));
            },
            puller: null, form: null, persist: _ =>
            {
                if (++saveAttempts == 1)
                {
                    throw new IOException("Temporary disk error");
                }

                return Task.CompletedTask;
            },
            batchUploader: batch ? (_, _) =>
            {
                sends++;
                return Task.FromResult<IReadOnlyList<FeatureSyncResult>>([FeatureSyncResult.Ok(17)]);
            } : null,
            canDispatchOrdinaryAdd: () => connected);

        Assert.Equal(0, await vm.SyncAsync());
        Assert.Equal(0, saveAttempts);
        Assert.Equal(0, sends);
        Assert.True(entry.IsPendingUpload);

        connected = true;
        Assert.Equal(0, await vm.SyncAsync());
        Assert.Equal(1, saveAttempts);
        Assert.Equal(0, sends);
        Assert.True(entry.IsPendingUpload);

        Assert.Equal(1, await vm.SyncAsync());
        Assert.Equal(1, sends);
        Assert.Equal(RecordSyncState.Synced, entry.SyncState);
    }

    [Fact]
    public async Task A_second_sync_while_one_is_running_is_a_no_op()
    {
        var gate = new TaskCompletionSource<FeatureSyncResult>();
        var calls = 0;
        var entries = new[] { PendingEntry("r1") };
        var vm = new SyncCenterViewModel(entries, (_, _) => { calls++; return gate.Task; });

        var first = vm.SyncAsync();
        var second = await vm.SyncAsync(); // re-entrant call returns immediately

        Assert.Equal(0, second);

        gate.SetResult(FeatureSyncResult.Ok(1));
        await first;

        Assert.Equal(1, calls); // the uploader ran exactly once
    }

    [Fact]
    public async Task SyncAsync_logs_warning_with_record_id_and_exception_type_when_persist_fails()
    {
        var record = new FieldRecord { RecordId = "rec-persist-fail", FormId = "f1", Status = RecordStatus.Submitted };
        record.Values["secret_field"] = "sensitive-field-value-do-not-log";
        var entry = new FieldRecordEntry(record);
        entry.MarkPending();

        var logger = new CaptureLogger<SyncCenterViewModel>();
        var vm = new SyncCenterViewModel(
            [entry],
            (_, _) => Task.FromResult(FeatureSyncResult.Ok(42)),
            puller: null,
            form: null,
            persist: e => e.SyncState == RecordSyncState.Synced
                ? throw new IOException("disk full")
                : Task.CompletedTask,
            logger: logger);

        var synced = await vm.SyncAsync();

        Assert.Equal(1, synced);
        Assert.Equal(RecordSyncState.Synced, entry.SyncState);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("rec-persist-fail", StringComparison.Ordinal)
            && e.Message.Contains("IOException", StringComparison.Ordinal));
        Assert.All(logger.Entries, e =>
            Assert.DoesNotContain("sensitive-field-value-do-not-log", e.Message, StringComparison.Ordinal));
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
