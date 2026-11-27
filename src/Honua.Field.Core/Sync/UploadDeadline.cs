// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Field.Core.Sync;

/// <summary>Bounds media uploads using a 60-second floor and an assumed uplink of 8 KiB/s.</summary>
internal static class UploadDeadline
{
    private const int MinimumBytesPerSecond = 8 * 1024;
    private const int MinimumMilliseconds = 60_000;
    // CancelAfter accepts at most uint.MaxValue - 1 milliseconds on .NET.
    private const uint MaximumMilliseconds = uint.MaxValue - 1;

    internal static CancellationTokenSource Start(long lengthBytes, CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(GetTimeout(lengthBytes));
        return deadline;
    }

    internal static TimeSpan GetTimeout(long lengthBytes)
    {
        var milliseconds = (double)lengthBytes * 1_000 / MinimumBytesPerSecond;
        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, MinimumMilliseconds, MaximumMilliseconds));
    }
}
