// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Records;
using Honua.Field.Presentation.Records;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Presentation.Tests;

public class RecordBoxViewModelTests
{
    private static FieldRecordEntry Entry(string id, RecordStatus status, bool synced = false, bool failed = false)
    {
        var entry = new FieldRecordEntry(new FieldRecord { RecordId = id, FormId = "f", Status = status });
        if (status != RecordStatus.Draft)
        {
            entry.MarkPending();
            if (synced)
            {
                entry.MarkSynced(remoteId: "srv-" + id);
            }
            else if (failed)
            {
                entry.MarkFailed("network down");
            }
        }

        return entry;
    }

    [Fact]
    public void Groups_records_into_drafts_outbox_sent()
    {
        var vm = new RecordBoxViewModel(
        [
            Entry("d1", RecordStatus.Draft),
            Entry("d2", RecordStatus.Draft),
            Entry("o1", RecordStatus.Submitted),
            Entry("o2", RecordStatus.Submitted, failed: true),
            Entry("s1", RecordStatus.Submitted, synced: true),
        ]);

        Assert.Equal(["d1", "d2"], vm.Drafts.Select(r => r.RecordId));
        Assert.Equal(["o1", "o2"], vm.Outbox.Select(r => r.RecordId));
        Assert.Equal(["s1"], vm.Sent.Select(r => r.RecordId));
        Assert.Equal("Drafts 2 · Outbox 2 · Conflicts 0 · Sent 1", vm.Header);
        Assert.Equal(2, vm.Summary.Drafts);
        Assert.Equal(1, vm.Summary.Failed);
    }

    [Fact]
    public void Visible_groups_preserve_empty_mailboxes_and_refresh_moved_records()
    {
        var pending = Entry("pending", RecordStatus.Submitted);
        var drafts = Enumerable.Range(0, 175).Select(index => Entry($"draft-{index}", RecordStatus.Draft));
        var vm = new RecordBoxViewModel(drafts.Append(pending));

        Assert.Equal(["Drafts", "Outbox", "Sent"], vm.VisibleGroups.Select(group => group.Title));
        Assert.Equal(175, vm.VisibleGroups[0].Count);
        Assert.Equal("draft-174", vm.VisibleGroups[0][174].Record?.RecordId);
        Assert.Equal("pending", Assert.Single(vm.VisibleGroups[1]).Record?.RecordId);
        Assert.Equal("Nothing sent yet.", Assert.Single(vm.VisibleGroups[2]).EmptyText);

        pending.MarkSynced("server-record");
        vm.Refresh();

        Assert.Equal("Outbox is empty.", Assert.Single(vm.VisibleGroups[1]).EmptyText);
        Assert.Equal("pending", Assert.Single(vm.VisibleGroups[2]).Record?.RecordId);
        Assert.Equal("Drafts 175 · Outbox 0 · Conflicts 0 · Sent 1", vm.Header);
    }

    [Fact]
    public void Status_text_reflects_each_box_state()
    {
        var draft = new RecordRowViewModel(Entry("d", RecordStatus.Draft));
        var sent = new RecordRowViewModel(Entry("s", RecordStatus.Submitted, synced: true));
        var failed = new RecordRowViewModel(Entry("o", RecordStatus.Submitted, failed: true));

        Assert.Equal("Draft", draft.StatusText);
        Assert.StartsWith("Sent", sent.StatusText);
        Assert.Contains("Failed", failed.StatusText);
    }
}
