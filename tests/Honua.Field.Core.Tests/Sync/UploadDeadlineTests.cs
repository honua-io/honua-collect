// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Sync;

namespace Honua.Field.Core.Tests.Sync;

public sealed class UploadDeadlineTests
{
    [Theory]
    [InlineData(0, 60_000)]
    [InlineData(10 * 1024, 60_000)]
    [InlineData(480 * 1024, 60_000)]
    [InlineData(481 * 1024, 60_125)]
    [InlineData(800 * 1024, 100_000)]
    [InlineData(long.MaxValue, 4_294_967_294L)]
    public void Upload_budget_is_bounded_by_the_minimum_and_maximum_timer_delays(long bytes, long milliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), UploadDeadline.GetTimeout(bytes));
        using var deadline = UploadDeadline.Start(bytes, CancellationToken.None);
        Assert.False(deadline.IsCancellationRequested);
    }
}
