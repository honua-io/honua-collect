// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

public sealed record InboxPage(IReadOnlyList<PulledRecord> Records, bool HasMore, int NextOffset);

public sealed record InboxPullCursor(int NextOffset, long PulledCount, bool Completed)
{
    public static InboxPullCursor Start { get; } = new(0, 0, false);
}

public interface IInboxPullProgressStore
{
    Task SaveProgressAsync(InboxPullCursor cursor, IReadOnlyList<PullClassification> page, CancellationToken ct = default);

    Task<InboxPullCursor> LoadCursorAsync(CancellationToken ct = default);
}

public sealed class InboxPullResult
{
    internal InboxPullResult(
        InboxPullCursor cursor,
        IReadOnlyList<PullClassification> classifications,
        long classifiedCount,
        long newCount,
        long conflictCount,
        bool faulted,
        string? error)
    {
        Cursor = cursor;
        Classifications = classifications;
        NewRecords = classifications.Where(c => c.Disposition == PullDisposition.New).ToList();
        Conflicts = classifications
            .Where(c => c.Disposition == PullDisposition.Conflict && c.Conflict is not null)
            .Select(c => c.Conflict!)
            .ToList();
        ClassifiedCount = classifiedCount;
        NewCount = newCount;
        ConflictCount = conflictCount;
        Faulted = faulted;
        Error = error;
    }

    public InboxPullCursor Cursor { get; }

    public IReadOnlyList<PullClassification> Classifications { get; }

    public IReadOnlyList<PullClassification> NewRecords { get; }

    public IReadOnlyList<RecordConflict> Conflicts { get; }

    public long ClassifiedCount { get; }

    public long NewCount { get; }

    public long ConflictCount { get; }

    public bool Faulted { get; }

    public string? Error { get; }

    public bool IsComplete => Cursor.Completed;
}

public sealed class PagedInboxPull
{
    private readonly FeaturePullService _merge = new();
    private readonly IInboxPullProgressStore _progress;

    public PagedInboxPull(IInboxPullProgressStore progress)
        => _progress = progress ?? throw new ArgumentNullException(nameof(progress));

    public async Task<InboxPullResult> ResumeAsync(
        FormDefinition form,
        IReadOnlyDictionary<long, FieldRecord> localByObjectId,
        Func<int, CancellationToken, Task<InboxPage>> fetchPageAsync,
        CancellationToken ct = default,
        Func<PullMergeResult, CancellationToken, Task>? onPageMerged = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(localByObjectId);
        ArgumentNullException.ThrowIfNull(fetchPageAsync);

        var cursor = await _progress.LoadCursorAsync(ct).ConfigureAwait(false);
        return await PullFromAsync(form, localByObjectId, fetchPageAsync, cursor, onPageMerged, ct).ConfigureAwait(false);
    }

    public Task<InboxPullResult> PullAllAsync(
        FormDefinition form,
        IReadOnlyDictionary<long, FieldRecord> localByObjectId,
        Func<int, CancellationToken, Task<InboxPage>> fetchPageAsync,
        CancellationToken ct = default,
        Func<PullMergeResult, CancellationToken, Task>? onPageMerged = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(localByObjectId);
        ArgumentNullException.ThrowIfNull(fetchPageAsync);

        return PullFromAsync(form, localByObjectId, fetchPageAsync, InboxPullCursor.Start, onPageMerged, ct);
    }

    private async Task<InboxPullResult> PullFromAsync(
        FormDefinition form,
        IReadOnlyDictionary<long, FieldRecord> localByObjectId,
        Func<int, CancellationToken, Task<InboxPage>> fetchPageAsync,
        InboxPullCursor cursor,
        Func<PullMergeResult, CancellationToken, Task>? onPageMerged,
        CancellationToken ct)
    {
        // In streaming mode (a per-page callback) we never accumulate the whole run, so a
        // million-row layer stays O(page); the running counts are kept either way so the
        // result is informative in both modes.
        var streaming = onPageMerged is not null;
        var runClassifications = streaming ? null : new List<PullClassification>();
        long classified = 0, newCount = 0, conflictCount = 0;

        InboxPullResult BuildResult(bool faulted, string? error) => new(
            cursor,
            (IReadOnlyList<PullClassification>?)runClassifications ?? Array.Empty<PullClassification>(),
            classified,
            newCount,
            conflictCount,
            faulted,
            error);

        if (cursor.Completed)
        {
            return BuildResult(faulted: false, error: null);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var offset = cursor.NextOffset;

            InboxPage page;
            try
            {
                page = await fetchPageAsync(offset, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A mid-run fetch failure must NOT discard already-merged pages: the
                // cursor and every prior page were persisted before this fetch, so we
                // return the partial result with the last good cursor. Resuming starts
                // at this same offset and re-fetches only this one (un-persisted) page.
                return BuildResult(faulted: true, error: ex.Message);
            }

            ArgumentNullException.ThrowIfNull(page);

            // De-dup WITHIN the page only (a server can repeat an id across an
            // overlapping page boundary). No cross-dataset "seen" set — that would
            // defeat bounded memory and bloat the persisted cursor.
            var pageRecords = DedupeWithinPage(page.Records);
            var pageMerge = _merge.Merge(form, pageRecords, localByObjectId);

            classified += pageMerge.Classifications.Count;
            newCount += pageMerge.NewRecords.Count;
            conflictCount += pageMerge.Conflicts.Count;

            if (streaming)
            {
                await onPageMerged!(pageMerge, ct).ConfigureAwait(false);
            }
            else
            {
                runClassifications!.AddRange(pageMerge.Classifications);
            }

            // Forward-progress guard: if the server claims more but doesn't advance the
            // offset, stop rather than loop forever.
            var advances = page.HasMore && page.NextOffset > offset;
            var nextCursor = new InboxPullCursor(
                NextOffset: advances ? page.NextOffset : offset,
                PulledCount: cursor.PulledCount + pageRecords.Count,
                Completed: !advances);

            // Persist progress for THIS page before fetching the next, so a later
            // fetch fault can't lose it.
            await _progress.SaveProgressAsync(nextCursor, pageMerge.Classifications, ct).ConfigureAwait(false);
            cursor = nextCursor;

            if (cursor.Completed)
            {
                return BuildResult(faulted: false, error: null);
            }
        }
    }

    private static IReadOnlyList<PulledRecord> DedupeWithinPage(IReadOnlyList<PulledRecord> records)
    {
        var seen = new HashSet<long>(records.Count);
        var result = new List<PulledRecord>(records.Count);
        foreach (var record in records)
        {
            if (seen.Add(record.ObjectId))
            {
                result.Add(record);
            }
        }

        return result;
    }
}
