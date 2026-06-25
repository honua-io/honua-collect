using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// One page of features pulled from the server for the editing inbox, plus the
/// paging signal needed to fetch the next page.
/// </summary>
/// <param name="Records">The decoded features in this page.</param>
/// <param name="HasMore">Whether the server has more features beyond this page.</param>
/// <param name="NextOffset">
/// The offset to request for the next page. Must strictly exceed the offset this page
/// was fetched at when <paramref name="HasMore"/> is true, or the pull stops to avoid
/// looping forever on a non-advancing server.
/// </param>
public sealed record InboxPage(IReadOnlyList<PulledRecord> Records, bool HasMore, int NextOffset);

/// <summary>
/// Durable paging cursor for a resumable inbox pull. It carries only the next offset
/// to request and a running count — deliberately <em>not</em> the set of object ids
/// already seen, so memory and the persisted cursor stay O(1) on a million-record
/// layer rather than growing without bound.
/// </summary>
/// <param name="NextOffset">Offset to request on the next page.</param>
/// <param name="PulledCount">Total records pulled across all pages so far.</param>
/// <param name="Completed">Whether the server has signalled there are no more pages.</param>
public sealed record InboxPullCursor(int NextOffset, long PulledCount, bool Completed)
{
    /// <summary>A fresh cursor starting at offset zero.</summary>
    public static InboxPullCursor Start { get; } = new(0, 0, false);
}

/// <summary>
/// Persists pull progress after each page so an interrupted run resumes without
/// re-pulling or losing already-merged pages. Implementations write the cursor and
/// the page's merged classifications to durable storage.
/// </summary>
public interface IInboxPullProgressStore
{
    /// <summary>
    /// Atomically records that a page was pulled and merged: advances the cursor and
    /// persists the page's classifications. Called once per page, before the next
    /// fetch, so a later fetch failure can't discard this page's progress.
    /// </summary>
    /// <param name="cursor">The cursor to resume from after this page.</param>
    /// <param name="page">The page's merge classifications.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveProgressAsync(InboxPullCursor cursor, IReadOnlyList<PullClassification> page, CancellationToken ct = default);

    /// <summary>Loads the last persisted cursor, or <see cref="InboxPullCursor.Start"/> when none.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<InboxPullCursor> LoadCursorAsync(CancellationToken ct = default);
}

/// <summary>
/// The result of a single <see cref="PagedInboxPull"/> run. Conflicts and new records
/// are those discovered <em>in this run only</em> — progress from earlier runs is
/// already persisted via <see cref="IInboxPullProgressStore"/>, so a caller wanting
/// the full picture reads it back from there rather than expecting this result to
/// re-surface prior runs' findings. <see cref="Cursor"/> reflects all runs.
/// </summary>
public sealed class InboxPullResult
{
    internal InboxPullResult(
        InboxPullCursor cursor,
        IReadOnlyList<PullClassification> classifications,
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
        Faulted = faulted;
        Error = error;
    }

    /// <summary>The cursor to resume from; <see cref="InboxPullCursor.Completed"/> when done.</summary>
    public InboxPullCursor Cursor { get; }

    /// <summary>Every feature classified in this run (this run's pages only).</summary>
    public IReadOnlyList<PullClassification> Classifications { get; }

    /// <summary>New-from-server features discovered in this run.</summary>
    public IReadOnlyList<PullClassification> NewRecords { get; }

    /// <summary>Conflicts discovered in this run.</summary>
    public IReadOnlyList<RecordConflict> Conflicts { get; }

    /// <summary>Whether a page fetch faulted mid-run (progress up to the fault is persisted).</summary>
    public bool Faulted { get; }

    /// <summary>The fault message when <see cref="Faulted"/>; otherwise null.</summary>
    public string? Error { get; }

    /// <summary>Whether the pull reached the end of the dataset.</summary>
    public bool IsComplete => Cursor.Completed;
}

/// <summary>
/// Robust, large-dataset paged pull of existing records into the editing inbox
/// (#38) — built to beat Survey123's failure to even load 2k→10k records for edit.
/// It pages through the server with a durable cursor and three hard guarantees:
///
/// <list type="number">
///   <item><description><b>Bounded memory.</b> Object ids are de-duplicated within a
///   single page only; the cursor never accumulates an all-records "seen" set, so a
///   million-row layer pulls in O(page) memory and the persisted cursor stays
///   tiny.</description></item>
///   <item><description><b>Incremental durability.</b> Each page is merged and its
///   progress persisted <em>before</em> the next fetch, so a fetch that throws
///   mid-run never discards already-merged pages — resuming continues from the next
///   un-pulled page rather than re-pulling from the start.</description></item>
///   <item><description><b>Forward-progress guard.</b> If the server reports more
///   pages but hands back a non-advancing offset, the pull stops instead of looping
///   forever.</description></item>
/// </list>
/// </summary>
public sealed class PagedInboxPull
{
    private readonly FeaturePullService _merge = new();
    private readonly IInboxPullProgressStore _progress;

    /// <summary>Creates the pull over a durable progress store.</summary>
    /// <param name="progress">Persists the cursor and merged pages after each page.</param>
    public PagedInboxPull(IInboxPullProgressStore progress)
        => _progress = progress ?? throw new ArgumentNullException(nameof(progress));

    /// <summary>
    /// Pulls pages starting from the persisted cursor until the dataset is exhausted,
    /// a page fetch faults, or the forward-progress guard trips. Every page is merged
    /// against the local store and its progress persisted before the next fetch, so
    /// the run is resumable: call again to continue from where it stopped.
    /// </summary>
    /// <param name="form">Form definition supplying field order/labels for diffing.</param>
    /// <param name="localByObjectId">Local records keyed by server object id.</param>
    /// <param name="fetchPageAsync">Fetches one page for a given offset.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>This run's result (classifications from this run only; cursor reflects all runs).</returns>
    public async Task<InboxPullResult> ResumeAsync(
        FormDefinition form,
        IReadOnlyDictionary<long, FieldRecord> localByObjectId,
        Func<int, CancellationToken, Task<InboxPage>> fetchPageAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(localByObjectId);
        ArgumentNullException.ThrowIfNull(fetchPageAsync);

        var cursor = await _progress.LoadCursorAsync(ct).ConfigureAwait(false);
        return await PullFromAsync(form, localByObjectId, fetchPageAsync, cursor, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls from a fresh cursor (offset 0), ignoring any persisted progress. Use to
    /// re-pull a layer from scratch.
    /// </summary>
    /// <param name="form">Form definition supplying field order/labels for diffing.</param>
    /// <param name="localByObjectId">Local records keyed by server object id.</param>
    /// <param name="fetchPageAsync">Fetches one page for a given offset.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>This run's result.</returns>
    public Task<InboxPullResult> PullAllAsync(
        FormDefinition form,
        IReadOnlyDictionary<long, FieldRecord> localByObjectId,
        Func<int, CancellationToken, Task<InboxPage>> fetchPageAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(localByObjectId);
        ArgumentNullException.ThrowIfNull(fetchPageAsync);

        return PullFromAsync(form, localByObjectId, fetchPageAsync, InboxPullCursor.Start, ct);
    }

    private async Task<InboxPullResult> PullFromAsync(
        FormDefinition form,
        IReadOnlyDictionary<long, FieldRecord> localByObjectId,
        Func<int, CancellationToken, Task<InboxPage>> fetchPageAsync,
        InboxPullCursor cursor,
        CancellationToken ct)
    {
        // Accumulates only THIS run's classifications. Prior runs' findings are already
        // persisted by the progress store — we never hold the whole dataset here.
        var runClassifications = new List<PullClassification>();

        if (cursor.Completed)
        {
            return new InboxPullResult(cursor, runClassifications, faulted: false, error: null);
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
                return new InboxPullResult(cursor, runClassifications, faulted: true, error: ex.Message);
            }

            ArgumentNullException.ThrowIfNull(page);

            // De-dup WITHIN the page only (a server can repeat an id across an
            // overlapping page boundary). No cross-dataset "seen" set — that would
            // defeat bounded memory and bloat the persisted cursor.
            var pageRecords = DedupeWithinPage(page.Records);
            var pageMerge = _merge.Merge(form, pageRecords, localByObjectId);
            runClassifications.AddRange(pageMerge.Classifications);

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
                return new InboxPullResult(cursor, runClassifications, faulted: false, error: null);
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
