// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Sync;

namespace Honua.Field.App.Services;

/// <summary>
/// Persists the O(1) <see cref="InboxPullCursor"/> in platform preferences, scoped
/// to the configured service and layer so changing targets never resumes at another
/// layer's offset.
/// </summary>
internal sealed class PreferencesInboxPullProgressStore : IInboxPullProgressStore
{
    private readonly string _keyPrefix;

    public PreferencesInboxPullProgressStore(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _keyPrefix = $"honua_collect_inbox_pull:{settings.Target.ServiceId}:{settings.Target.LayerId}:";
    }

    public Task SaveProgressAsync(
        InboxPullCursor cursor,
        IReadOnlyList<PullClassification> page,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Preferences.Default.Set(_keyPrefix + "next_offset", cursor.NextOffset);
        Preferences.Default.Set(_keyPrefix + "pulled_count", cursor.PulledCount);
        Preferences.Default.Set(_keyPrefix + "completed", cursor.Completed);
        return Task.CompletedTask;
    }

    public Task<InboxPullCursor> LoadCursorAsync(CancellationToken ct = default)
    {
        var nextOffset = Preferences.Default.Get(_keyPrefix + "next_offset", 0);
        var pulledCount = Preferences.Default.Get(_keyPrefix + "pulled_count", 0L);
        var completed = Preferences.Default.Get(_keyPrefix + "completed", false);
        return Task.FromResult(new InboxPullCursor(nextOffset, pulledCount, completed));
    }
}
