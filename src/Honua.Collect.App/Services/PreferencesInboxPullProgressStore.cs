// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Sync;

namespace Honua.Collect.App.Services;

/// <summary>
/// Persists the O(1) <see cref="InboxPullCursor"/> in platform preferences so an
/// interrupted paged inbox pull resumes from the last completed page across restarts.
/// </summary>
internal sealed class PreferencesInboxPullProgressStore : IInboxPullProgressStore
{
    private const string OffsetKey = "honua_collect_inbox_pull_next_offset";
    private const string CountKey = "honua_collect_inbox_pull_pulled_count";
    private const string CompletedKey = "honua_collect_inbox_pull_completed";

    public Task SaveProgressAsync(
        InboxPullCursor cursor,
        IReadOnlyList<PullClassification> page,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Preferences.Default.Set(OffsetKey, cursor.NextOffset);
        Preferences.Default.Set(CountKey, cursor.PulledCount);
        Preferences.Default.Set(CompletedKey, cursor.Completed);
        return Task.CompletedTask;
    }

    public Task<InboxPullCursor> LoadCursorAsync(CancellationToken ct = default)
    {
        var nextOffset = Preferences.Default.Get(OffsetKey, 0);
        var pulledCount = Preferences.Default.Get(CountKey, 0L);
        var completed = Preferences.Default.Get(CompletedKey, false);
        return Task.FromResult(new InboxPullCursor(nextOffset, pulledCount, completed));
    }
}
