// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Maps;

public readonly record struct TileCoordinate(int Zoom, int X, int Y)
{
    public override string ToString() => $"{Zoom}/{X}/{Y}";
}
