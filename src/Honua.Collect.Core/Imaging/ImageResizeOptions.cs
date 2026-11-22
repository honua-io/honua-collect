// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Imaging;

public enum ImageEncodeFormat
{
    Jpeg = 0,

    Png = 1,

    Webp = 2,
}

public sealed record ImageResizeOptions
{
    public const int DefaultMaxEdge = 1600;

    public const int DefaultQuality = 75;

    public int MaxEdge { get; init; } = DefaultMaxEdge;

    public int Quality { get; init; } = DefaultQuality;

    public ImageEncodeFormat Format { get; init; } = ImageEncodeFormat.Jpeg;

    public bool SkipWhenWithinBudget { get; init; } = true;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEdge);
        ArgumentOutOfRangeException.ThrowIfLessThan(Quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Quality, 100);
    }
}
