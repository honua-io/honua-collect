using Honua.Collect.Core.Sync;
using Honua.Collect.Core.Tests.TestData;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

/// <summary>
/// Robust large-dataset paged inbox pull (#38). Pins the four known pitfalls shut:
/// bounded memory (no unbounded seen-set), incremental durability (a mid-run throw
/// keeps merged pages and resumes without re-pulling), the forward-progress guard
/// (non-advancing offset terminates), and an honest multi-run result contract.
/// </summary>
public sealed class PagedInboxPullTests
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

    private static PulledRecord Pulled(long objectId)
        => new(objectId, FieldRecords.WithValues($"r{objectId}", ("name", $"n{objectId}")));

    /// <summary>An in-memory progress store that records every save so the test can
    /// assert incremental persistence and bounded cursor size.</summary>
    private sealed class FakeProgress : IInboxPullProgressStore
    {
        public InboxPullCursor Cursor { get; private set; } = InboxPullCursor.Start;
        public List<PullClassification> Persisted { get; } = [];
        public int SaveCount { get; private set; }

        public Task SaveProgressAsync(
            InboxPullCursor cursor, IReadOnlyList<PullClassification> page, CancellationToken ct = default)
        {
            Cursor = cursor;
            Persisted.AddRange(page);
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<InboxPullCursor> LoadCursorAsync(CancellationToken ct = default)
            => Task.FromResult(Cursor);
    }

    [Fact]
    public async Task Pulls_every_page_to_completion()
    {
        var progress = new FakeProgress();
        var pull = new PagedInboxPull(progress);

        Task<InboxPage> Fetch(int offset, CancellationToken ct) => offset switch
        {
            0 => Task.FromResult(new InboxPage([Pulled(1), Pulled(2)], HasMore: true, NextOffset: 2)),
            2 => Task.FromResult(new InboxPage([Pulled(3)], HasMore: false, NextOffset: 3)),
            _ => throw new InvalidOperationException($"unexpected offset {offset}"),
        };

        var result = await pull.PullAllAsync(Form(), new Dictionary<long, FieldRecord>(), Fetch);

        Assert.True(result.IsComplete);
        Assert.Equal(3, result.Classifications.Count);
        Assert.Equal(3, result.Cursor.PulledCount);
    }

    [Fact]
    public async Task Cursor_is_bounded_and_does_not_carry_a_seen_set()
    {
        // The cursor is a small fixed-size record (offset + count + flag). It must NOT
        // grow with the dataset. We assert structurally: even after many records, the
        // cursor exposes only those scalar fields and no id collection.
        var props = typeof(InboxPullCursor).GetProperties();
        Assert.DoesNotContain(props, p =>
            typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType)
            && p.PropertyType != typeof(string));
    }

    [Fact]
    public async Task A_fetch_throw_mid_run_keeps_merged_pages_and_persists_progress()
    {
        var progress = new FakeProgress();
        var pull = new PagedInboxPull(progress);

        Task<InboxPage> Fetch(int offset, CancellationToken ct) => offset switch
        {
            0 => Task.FromResult(new InboxPage([Pulled(1), Pulled(2)], HasMore: true, NextOffset: 2)),
            2 => throw new HttpRequestException("network died"),
            _ => throw new InvalidOperationException($"unexpected offset {offset}"),
        };

        var result = await pull.PullAllAsync(Form(), new Dictionary<long, FieldRecord>(), Fetch);

        // The run faulted, but the first page's two records are merged AND persisted,
        // not discarded.
        Assert.True(result.Faulted);
        Assert.Equal("network died", result.Error);
        Assert.Equal(2, result.Classifications.Count);
        Assert.Equal(2, progress.Persisted.Count);
        // The cursor advanced to offset 2 so a resume re-fetches only that page.
        Assert.Equal(2, progress.Cursor.NextOffset);
        Assert.False(progress.Cursor.Completed);
    }

    [Fact]
    public async Task Resume_continues_from_the_persisted_cursor_without_re_pulling()
    {
        var progress = new FakeProgress();
        var pull = new PagedInboxPull(progress);
        var fetchedOffsets = new List<int>();

        Task<InboxPage> Fetch(int offset, CancellationToken ct)
        {
            fetchedOffsets.Add(offset);
            return offset switch
            {
                0 => Task.FromResult(new InboxPage([Pulled(1)], HasMore: true, NextOffset: 1)),
                1 => offset == 1 && fetchedOffsets.Count(o => o == 1) == 1
                    ? throw new HttpRequestException("boom")
                    : Task.FromResult(new InboxPage([Pulled(2)], HasMore: false, NextOffset: 2)),
                _ => throw new InvalidOperationException($"unexpected offset {offset}"),
            };
        }

        // First run faults on the second page.
        var first = await pull.ResumeAsync(Form(), new Dictionary<long, FieldRecord>(), Fetch);
        Assert.True(first.Faulted);
        Assert.Single(first.Classifications); // only record 1

        // Resume: continues at offset 1, does not re-pull offset 0.
        var second = await pull.ResumeAsync(Form(), new Dictionary<long, FieldRecord>(), Fetch);
        Assert.True(second.IsComplete);
        Assert.Single(second.Classifications); // only record 2, this run
        Assert.Equal(new[] { 0, 1, 1 }, fetchedOffsets.ToArray()); // offset 0 fetched ONCE
        Assert.Equal(2, progress.Cursor.PulledCount); // 1 + 1 across both runs
    }

    [Fact]
    public async Task Non_advancing_offset_terminates_instead_of_looping_forever()
    {
        var progress = new FakeProgress();
        var pull = new PagedInboxPull(progress);
        var calls = 0;

        // HasMore is true but NextOffset never exceeds the requested offset.
        Task<InboxPage> Fetch(int offset, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new InboxPage([Pulled(1)], HasMore: true, NextOffset: offset));
        }

        var result = await pull.PullAllAsync(Form(), new Dictionary<long, FieldRecord>(), Fetch);

        Assert.True(result.IsComplete); // guard tripped -> treated as done
        Assert.Equal(1, calls);          // did NOT loop
    }

    [Fact]
    public async Task Duplicate_ids_within_a_page_are_de_duplicated()
    {
        var progress = new FakeProgress();
        var pull = new PagedInboxPull(progress);

        Task<InboxPage> Fetch(int offset, CancellationToken ct)
            => Task.FromResult(new InboxPage([Pulled(1), Pulled(1), Pulled(2)], HasMore: false, NextOffset: 1));

        var result = await pull.PullAllAsync(Form(), new Dictionary<long, FieldRecord>(), Fetch);

        Assert.Equal(2, result.Classifications.Count); // 1 and 2, the dup dropped
        Assert.Equal(2, result.Cursor.PulledCount);
    }

    [Fact]
    public async Task Result_surfaces_new_records_and_conflicts_for_this_run()
    {
        var progress = new FakeProgress();
        var pull = new PagedInboxPull(progress);

        // Local record for object 1 with a differing value -> conflict; object 2 is new.
        var local = new Dictionary<long, FieldRecord>
        {
            [1] = FieldRecords.WithValues("r1", ("name", "local-different")),
        };

        Task<InboxPage> Fetch(int offset, CancellationToken ct)
            => Task.FromResult(new InboxPage([Pulled(1), Pulled(2)], HasMore: false, NextOffset: 2));

        var result = await pull.PullAllAsync(Form(), local, Fetch);

        Assert.Single(result.Conflicts);
        Assert.Single(result.NewRecords);
    }

    [Fact]
    public async Task Resuming_a_completed_cursor_is_a_no_op()
    {
        var progress = new FakeProgress();
        await progress.SaveProgressAsync(new InboxPullCursor(10, 10, Completed: true), []);
        var pull = new PagedInboxPull(progress);
        var called = false;

        Task<InboxPage> Fetch(int offset, CancellationToken ct)
        {
            called = true;
            return Task.FromResult(new InboxPage([], false, offset));
        }

        var result = await pull.ResumeAsync(Form(), new Dictionary<long, FieldRecord>(), Fetch);

        Assert.True(result.IsComplete);
        Assert.False(called);
    }
}
