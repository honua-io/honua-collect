// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Imaging;

public interface IImageResizer
{
    ImageResizeResult Resize(byte[] source, ImageResizeOptions options);
}

public sealed class ImageDecodeException : Exception
{
    public ImageDecodeException(string message) : base(message)
    {
    }
}
