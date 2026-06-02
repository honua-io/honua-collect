namespace Honua.Collect.Core.Sync;

/// <summary>A contiguous byte range of a media file to upload as one chunk.</summary>
/// <param name="Index">Zero-based chunk index.</param>
/// <param name="Offset">Byte offset into the file.</param>
/// <param name="Length">Number of bytes in the chunk.</param>
public readonly record struct UploadChunk(int Index, long Offset, long Length);

/// <summary>
/// Plans and tracks a chunked, resumable upload of a large media file (BACKLOG
/// C9). Large photos/videos captured in the field are split into fixed-size
/// chunks; completed chunks are recorded so a dropped connection resumes from
/// where it left off instead of restarting. The transport itself is the host's
/// job — this owns the chunk math and progress.
/// </summary>
public sealed class ResumableUpload
{
    private readonly HashSet<int> _completed = [];

    /// <summary>Creates an upload plan for a file.</summary>
    /// <param name="totalBytes">Total size of the file in bytes.</param>
    /// <param name="chunkSize">Chunk size in bytes.</param>
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

    /// <summary>Total file size in bytes.</summary>
    public long TotalBytes { get; }

    /// <summary>Chunk size in bytes.</summary>
    public long ChunkSize { get; }

    /// <summary>Total number of chunks.</summary>
    public int ChunkCount { get; }

    /// <summary>Number of chunks uploaded so far.</summary>
    public int CompletedCount => _completed.Count;

    /// <summary>Whether every chunk has been uploaded.</summary>
    public bool IsComplete => _completed.Count == ChunkCount;

    /// <summary>Upload progress in the range 0..1.</summary>
    public double Progress => ChunkCount == 0 ? 1.0 : (double)_completed.Count / ChunkCount;

    /// <summary>The byte range for a chunk index.</summary>
    /// <param name="index">Chunk index.</param>
    /// <returns>The chunk descriptor.</returns>
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

    /// <summary>Marks a chunk as successfully uploaded.</summary>
    /// <param name="index">Chunk index.</param>
    public void MarkUploaded(int index)
    {
        if (index < 0 || index >= ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _completed.Add(index);
    }

    /// <summary>The chunks still needing upload, in order — the resume work list.</summary>
    /// <returns>Pending chunks.</returns>
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

    /// <summary>Restores progress from a persisted set of completed chunk indices.</summary>
    /// <param name="completedIndices">Indices already uploaded.</param>
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
}
