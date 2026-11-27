// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Field.Core.Maps;

/// <summary>The outcome of verifying or caching the tiles required by an offline area.</summary>
public sealed record OfflineAreaDownload(OfflineAreaPlan Plan, int Cached)
{
    public bool IsReady => !Plan.ExceedsCap && Plan.Count > 0 && Cached == Plan.Count;

    public static async Task<OfflineAreaDownload> RunAsync(
        OfflineAreaPlan plan,
        Func<TileCoordinate, CancellationToken, ValueTask<bool>> ensureCached,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = 0;
        if (!plan.ExceedsCap)
        {
            await Parallel.ForEachAsync(plan.Tiles, new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = cancellationToken,
            }, async (tile, token) =>
            {
                if (await ensureCached(tile, token).ConfigureAwait(false))
                {
                    var count = Interlocked.Increment(ref cached);
                    progress?.Report(count);
                }
            }).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new OfflineAreaDownload(plan, cached);
    }
}
