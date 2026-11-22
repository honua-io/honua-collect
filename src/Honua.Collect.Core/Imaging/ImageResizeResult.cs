// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Imaging;

public readonly record struct ImageResizeResult(
    byte[] Bytes,
    int Width,
    int Height,
    ImageEncodeFormat Format,
    bool Resized,
    bool ReEncoded)
{
    public int ByteLength => Bytes.Length;
}
