// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Sync;
using Honua.Field.Presentation.Sync;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Presentation.Tests;

/// <summary>
/// Covers <see cref="PagedInboxPullViewModel"/> (BACKLOG #38 / #101): cursor hydration,
/// multi-page streaming pull, mid-run fault recovery and durable resume without
/// re-pulling completed pages, and pull-from-start reset.
/// </summary>
public sealed class PagedInboxPullViewModelTests
{
    private sealed class InMemoryProgressStore : IInboxPullProgressStore
    {
        public InboxPullCursor Cursor { get; set; } = InboxPullCursor.Start;
        public List<IReadOnlyList<PullClassification>> SavedPages { get; } = [];

        public Task SaveProgressAsync(InboxPullCursor cursor, IReadOnlyList<PullClassification> page, CancellationToken ct = default)
        {
            Cursor = cursor;
            SavedPages.Add(page);
            return Task.CompletedTask;
        }

        public Task<InboxPullCursor> LoadCursorAsync(CancellationToken ct = default)
            => Task.FromResult(Cursor);
    }

    private static FormDefinition CreateForm() => new()
    {
        FormId = "site_check",
        Name = "Site Check",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields = [new FormField { FieldId = "status", Label = "Status", Type = FormFieldType.Text }],
            },
        ],
    };

    private static PulledRecord ServerRecord(long objectId, string status)
    {
        var record = new FieldRecord { RecordId = $"srv-{objectId}", FormId = "site_check" };
        record.Values["status"] = status;
        return new PulledRecord(objectId, record);
    }

    [Fact]
    public async Task Fault_mid_run_preserves_cursor_and_resume_finishes_without_repulling_first_page()
    {
        var store = new InMemoryProgressStore();
        var localRecord = new FieldRecord { RecordId = "local-2", FormId = "site_check" };
        localRecord.Values["status"] = "local-edit";
        var localIndex = new Dictionary<long, FieldRecord> { [2] = localRecord };

        var requestedOffsets = new List<int>();
        var failSecondPageOnce = true;

        var vm = new PagedInboxPullViewModel(
            store,
            CreateForm(),
            () => localIndex,
            (offset, _) =>
            {
                requestedOffsets.Add(offset);
                if (offset == 0)
                {
                    return Task.FromResult(new InboxPage([ServerRecord(1, "ok")], HasMore: true, NextOffset: 1));
                }

                if (failSecondPageOnce)
                {
                    failSecondPageOnce = false;
                    throw new IOException("connection reset");
                }

                return Task.FromResult(new InboxPage([ServerRecord(2, "server-edit")], HasMore: false, NextOffset: 2));
            });

        Assert.Equal("Ready to pull inbox pages.", vm.StatusSummary);
        Assert.True(vm.ResumeCommand.CanExecute(null));
        Assert.True(vm.PullFromStartCommand.CanExecute(null));

        // First run succeeds on page 0 and faults on page 1.
        var first = await vm.ResumePullAsync();
        Assert.NotNull(first);
        Assert.True(first!.Faulted);
        Assert.True(vm.IsFaulted);
        Assert.Equal("connection reset", vm.LastError);
        Assert.True(vm.HasResumableProgress);
        Assert.False(vm.IsComplete);
        Assert.Equal(1, vm.PulledCount);
        Assert.Equal(1, vm.NewCount);
        Assert.Equal(0, vm.ConflictCount);
        Assert.Contains("Paused after error", vm.StatusSummary);
        Assert.Single(vm.LatestPageRows);

        // Fresh VM hydrates the durable cursor and shows resumable status immediately.
        var resumedVm = new PagedInboxPullViewModel(
            store,
            CreateForm(),
            () => localIndex,
            (offset, _) =>
            {
                requestedOffsets.Add(offset);
                return Task.FromResult(new InboxPage([ServerRecord(2, "server-edit")], HasMore: false, NextOffset: 2));
            });

        await resumedVm.LoadCursorAsync();
        Assert.True(resumedVm.HasResumableProgress);
        Assert.Contains("Resumable from offset 1", resumedVm.StatusSummary);

        // Resume picks up at offset 1 (never re-requests offset 0) and surfaces the conflict.
        var second = await resumedVm.ResumePullAsync();
        Assert.NotNull(second);
        Assert.False(second!.Faulted);
        Assert.True(resumedVm.IsComplete);
        Assert.False(resumedVm.IsFaulted);
        Assert.Null(resumedVm.LastError);
        Assert.Equal(2, resumedVm.PulledCount);
        Assert.Equal(1, resumedVm.ConflictCount);
        Assert.Single(resumedVm.Conflicts);
        Assert.Single(resumedVm.LatestPageRows);
        Assert.Contains("Complete:", resumedVm.StatusSummary);
        Assert.Equal([0, 1, 1], requestedOffsets);
    }

    [Fact]
    public async Task PullFromStartAsync_resets_session_counts_and_restarts_from_offset_zero()
    {
        var store = new InMemoryProgressStore
        {
            Cursor = new InboxPullCursor(NextOffset: 5, PulledCount: 5, Completed: true),
        };
        var offsets = new List<int>();
        var vm = new PagedInboxPullViewModel(
            store,
            CreateForm(),
            () => new Dictionary<long, FieldRecord>(),
            (offset, _) =>
            {
                offsets.Add(offset);
                return Task.FromResult(new InboxPage([ServerRecord(10, "new")], HasMore: false, NextOffset: 1));
            });

        await vm.LoadCursorAsync();
        Assert.True(vm.IsComplete);

        var result = await vm.PullFromStartAsync();
        Assert.NotNull(result);
        Assert.Equal([0], offsets);
        Assert.Equal(1, vm.PulledCount);
        Assert.Equal(1, vm.NewCount);
        Assert.Single(vm.LatestPageRows);
    }
}
