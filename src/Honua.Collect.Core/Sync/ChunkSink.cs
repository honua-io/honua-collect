// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Sync;

public interface IChunkSink
{
    Task SendAsync(UploadChunk chunk, ReadOnlyMemory<byte> bytes, string chunkSha256Hex, CancellationToken cancellationToken);
}

public sealed class ChunkUploadException : Exception
{
    public ChunkUploadException(string message) : base(message)
    {
    }

    public ChunkUploadException(string message, Exception inner) : base(message, inner)
    {
    }
}
