// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Maps;

namespace Honua.Field.Core.Tests.Maps;

public sealed class OfflineAreaDownloadTests
{
    [Fact]
    public async Task Partial_area_remains_unready_after_reopen_and_retry_fills_only_missing_tiles()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var plan = OfflineAreaPlanner.Plan(new GeoBoundingBox(-10, -10, 10, 10), 2, 2);
            var cache = new TileCache(root.FullName);
            var retained = plan.Tiles[0];

            var partial = await OfflineAreaDownload.RunAsync(plan, async (tile, token) =>
            {
                if (tile != retained)
                {
                    return false;
                }

                await cache.SaveAsync(tile.Zoom, tile.X, tile.Y, [1], token);
                return true;
            });

            Assert.Equal(1, partial.Cached);
            Assert.False(partial.IsReady);

            cache = new TileCache(root.FullName);
            var fetched = 0;
            var retried = await OfflineAreaDownload.RunAsync(plan, async (tile, token) =>
            {
                if (!cache.Contains(tile.Zoom, tile.X, tile.Y))
                {
                    Interlocked.Increment(ref fetched);
                    await cache.SaveAsync(tile.Zoom, tile.X, tile.Y, [1], token);
                }

                return true;
            });

            Assert.True(retried.IsReady);
            Assert.Equal(plan.Count - 1, fetched);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Cap_refuses_area_without_attempting_any_tiles()
    {
        var plan = OfflineAreaPlanner.Plan(new GeoBoundingBox(-10, -10, 10, 10), 2, 2, maxTiles: 1);

        var result = await OfflineAreaDownload.RunAsync(plan,
            (_, _) => throw new InvalidOperationException("Oversized plan attempted a tile."));

        Assert.True(result.Plan.ExceedsCap);
        Assert.False(result.IsReady);
        Assert.Equal(0, result.Cached);
    }

    [Fact]
    public async Task Cancelled_area_does_not_return_ready_even_if_a_tile_finishes()
    {
        using var cancellation = new CancellationTokenSource();
        var plan = OfflineAreaPlanner.Plan(new GeoBoundingBox(-10, -10, 10, 10), 2, 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineAreaDownload.RunAsync(plan, (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(true);
            }, cancellationToken: cancellation.Token));
    }
}
