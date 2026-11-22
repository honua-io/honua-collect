// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace Honua.Collect.Core.Sync;

public readonly record struct UploadChunk(int Index, long Offset, long Length);

public enum ResumableUploadState
{
    Pending = 0,

    InProgress = 1,

    Completed = 2,
}

public sealed class ResumableUpload
{
    private readonly HashSet<int> _completed = [];
    private readonly Dictionary<int, string> _chunkHashes = [];

    public ResumableUpload(long totalBytes, long chunkSize)
    {
        if (totalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }

        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        TotalBytes = totalBytes;
        ChunkSize = chunkSize;
        ChunkCount = totalBytes == 0 ? 0 : (int)((totalBytes + chunkSize - 1) / chunkSize);
    }

    public long TotalBytes { get; }

    public long ChunkSize { get; }

    public int ChunkCount { get; }

    public int CompletedCount => _completed.Count;

    public bool IsComplete => _completed.Count == ChunkCount;

    public ResumableUploadState State => _completed.Count switch
    {
        0 => ChunkCount == 0 ? ResumableUploadState.Completed : ResumableUploadState.Pending,
        var n when n == ChunkCount => ResumableUploadState.Completed,
        _ => ResumableUploadState.InProgress,
    };

    public long UploadedBytes
    {
        get
        {
            long total = 0;
            foreach (var index in _completed)
            {
                total += ChunkAt(index).Length;
            }

            return total;
        }
    }

    public double Progress => ChunkCount == 0 ? 1.0 : (double)_completed.Count / ChunkCount;

    public UploadChunk ChunkAt(int index)
    {
        if (index < 0 || index >= ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var offset = index * ChunkSize;
        var length = Math.Min(ChunkSize, TotalBytes - offset);
        return new UploadChunk(index, offset, length);
    }

    public bool IsChunkComplete(int index)
    {
        if (index < 0 || index >= ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _completed.Contains(index);
    }

    public void MarkUploaded(int index) => MarkUploaded(index, chunkHashHex: null);

    public void MarkUploaded(int index, string? chunkHashHex)
    {
        if (index < 0 || index >= ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _completed.Add(index);
        if (chunkHashHex is not null)
        {
            _chunkHashes[index] = chunkHashHex;
        }
    }

    public string? ChunkHash(int index)
    {
        if (index < 0 || index >= ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _chunkHashes.GetValueOrDefault(index);
    }

    public UploadChunk? NextPending()
    {
        for (var i = 0; i < ChunkCount; i++)
        {
            if (!_completed.Contains(i))
            {
                return ChunkAt(i);
            }
        }

        return null;
    }

    public IReadOnlyList<UploadChunk> PendingChunks()
    {
        var pending = new List<UploadChunk>();
        for (var i = 0; i < ChunkCount; i++)
        {
            if (!_completed.Contains(i))
            {
                pending.Add(ChunkAt(i));
            }
        }

        return pending;
    }

    public void Resume(IEnumerable<int> completedIndices)
    {
        ArgumentNullException.ThrowIfNull(completedIndices);
        foreach (var index in completedIndices)
        {
            if (index >= 0 && index < ChunkCount)
            {
                _completed.Add(index);
            }
        }
    }

    public bool VerifyFinalIntegrity(byte[] content, string expectedSha256Hex)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(expectedSha256Hex);
        if (!IsComplete)
        {
            throw new InvalidOperationException("Cannot verify integrity before all chunks are uploaded.");
        }

        if (content.LongLength != TotalBytes)
        {
            throw new ArgumentException(
                $"Content length {content.LongLength} does not match the planned total {TotalBytes}.",
                nameof(content));
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(content));
        return string.Equals(actual, expectedSha256Hex, StringComparison.OrdinalIgnoreCase);
    }

    public bool VerifyFinalIntegrity(Stream content, string expectedSha256Hex)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(expectedSha256Hex);
        if (!IsComplete)
        {
            throw new InvalidOperationException("Cannot verify integrity before all chunks are uploaded.");
        }

        if (!content.CanSeek)
        {
            throw new ArgumentException("The content stream must be seekable to verify whole-file integrity.", nameof(content));
        }

        if (content.Length != TotalBytes)
        {
            throw new ArgumentException(
                $"Content length {content.Length} does not match the planned total {TotalBytes}.",
                nameof(content));
        }

        content.Position = 0;
        var actual = Convert.ToHexStringLower(SHA256.HashData(content));
        return string.Equals(actual, expectedSha256Hex, StringComparison.OrdinalIgnoreCase);
    }
}
