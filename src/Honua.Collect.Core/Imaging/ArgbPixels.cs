// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Imaging;

public static class ArgbPixels
{
    public static byte[] PackRgb(int[] argbPixels)
    {
        ArgumentNullException.ThrowIfNull(argbPixels);

        var rgb = new byte[argbPixels.Length * 3];
        for (var i = 0; i < argbPixels.Length; i++)
        {
            var p = argbPixels[i];
            rgb[(i * 3) + 0] = (byte)((p >> 16) & 0xFF); // R
            rgb[(i * 3) + 1] = (byte)((p >> 8) & 0xFF);  // G
            rgb[(i * 3) + 2] = (byte)(p & 0xFF);         // B
        }

        return rgb;
    }
}
