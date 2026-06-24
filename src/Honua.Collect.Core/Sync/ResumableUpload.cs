using System.Security.Cryptography;

namespace Honua.Collect.Core.Sync;

/// <summary>A contiguous byte range of a media file to upload as one chunk.</summary>
/// <param name="Index">Zero-based chunk index.</param>
/// <param name="Offset">Byte offset into the file.</param>
/// <param name="Length">Number of bytes in the chunk.</param>
public readonly record struct UploadChunk(int Index, long Offset, long Length);

/// <summary>The lifecycle state of a <see cref="ResumableUpload"/>.</summary>
public enum ResumableUploadState
{
    /// <summary>No chunks uploaded yet (and the file is non-empty).</summary>
    Pending = 0,

    /// <summary>At least one — but not all — chunks uploaded.</summary>
    InProgress = 1,

    /// <summary>Every chunk uploaded; ready for the final integrity check / commit.</summary>
    Completed = 2,
}

/// <summary>
/// Plans and tracks a chunked, resumable upload of a large media file (BACKLOG
/// C9). Large photos/videos captured in the field are split into fixed-size
/// chunks; completed chunks are recorded so a dropped connection resumes from
/// where it left off instead of restarting. The transport itself is the host's
/// job (see <see cref="ResumableUploadDriver"/>) — this owns the chunk maths,
/// progress, per-chunk and whole-file integrity, and the lifecycle state machine.
/// </summary>
public sealed class ResumableUpload
{
    private readonly HashSet<int> _completed = [];
    private readonly Dictionary<int, string> _chunkHashes = [];

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

    /// <summary>The lifecycle state derived from how many chunks are complete.</summary>
    public ResumableUploadState State => _completed.Count switch
    {
        0 => ChunkCount == 0 ? ResumableUploadState.Completed : ResumableUploadState.Pending,
        var n when n == ChunkCount => ResumableUploadState.Completed,
        _ => ResumableUploadState.InProgress,
    };

    /// <summary>Number of bytes uploaded so far (sum of completed chunk lengths).</summary>
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

    /// <summary>Whether a chunk index has been recorded as uploaded.</summary>
    /// <param name="index">Chunk index.</param>
    /// <returns><see langword="true"/> when the chunk is complete.</returns>
    public bool IsChunkComplete(int index)
    {
        if (index < 0 || index >= ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _completed.Contains(index);
    }

    /// <summary>Marks a chunk as successfully uploaded.</summary>
    /// <param name="index">Chunk index.</param>
    public void MarkUploaded(int index) => MarkUploaded(index, chunkHashHex: null);

    /// <summary>
    /// Marks a chunk as successfully uploaded and records its per-chunk integrity
    /// digest so a later resume can verify the byte range still hashes the same.
    /// </summary>
    /// <param name="index">Chunk index.</param>
    /// <param name="chunkHashHex">Lowercase-hex SHA-256 of the chunk bytes, or null.</param>
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

    /// <summary>The recorded per-chunk integrity digest, or null if none was stored.</summary>
    /// <param name="index">Chunk index.</param>
    /// <returns>The lowercase-hex SHA-256 of the chunk, or null.</returns>
    public string? ChunkHash(int index)
    {
        if (index < 0 || index >= ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _chunkHashes.GetValueOrDefault(index);
    }

    /// <summary>The next chunk still needing upload, or null when complete — the resume cursor.</summary>
    /// <returns>The lowest-index pending chunk, or null.</returns>
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

    /// <summary>
    /// Verifies the whole-file integrity once <see cref="IsComplete"/>: hashes the full
    /// <paramref name="content"/> and compares it to the expected digest. This is the
    /// final gate before the server is told to commit the assembled file.
    /// </summary>
    /// <param name="content">The full file bytes (length must equal <see cref="TotalBytes"/>).</param>
    /// <param name="expectedSha256Hex">The expected lowercase-hex SHA-256 of the whole file.</param>
    /// <returns><see langword="true"/> when complete and the digest matches.</returns>
    /// <exception cref="InvalidOperationException">If the upload is not yet complete.</exception>
    /// <exception cref="ArgumentException">If <paramref name="content"/> length != <see cref="TotalBytes"/>.</exception>
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
}
