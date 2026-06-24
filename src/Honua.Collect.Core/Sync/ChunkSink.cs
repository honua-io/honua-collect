namespace Honua.Collect.Core.Sync;

/// <summary>
/// The transport boundary the <see cref="ResumableUploadDriver"/> drives: a single
/// method that accepts one chunk's bytes at a known file offset and either acknowledges
/// it or throws to signal a transient failure the driver should retry. Keeping the
/// transport behind this seam lets the resumable-upload logic be unit-tested against a
/// fake/in-memory sink with no live server. The production binding is an HTTP sink that
/// PUTs the byte range to the media endpoint.
/// </summary>
public interface IChunkSink
{
    /// <summary>
    /// Sends one chunk to the destination.
    /// </summary>
    /// <param name="chunk">The chunk descriptor (index, offset, length).</param>
    /// <param name="bytes">The chunk's bytes (length == <see cref="UploadChunk.Length"/>).</param>
    /// <param name="chunkSha256Hex">Lowercase-hex SHA-256 of <paramref name="bytes"/> for server-side per-chunk verification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the chunk is durably accepted.</returns>
    /// <exception cref="ChunkUploadException">Thrown to signal a transient failure the driver may retry.</exception>
    Task SendAsync(UploadChunk chunk, ReadOnlyMemory<byte> bytes, string chunkSha256Hex, CancellationToken cancellationToken);
}

/// <summary>
/// A transient chunk-transport failure (network blip, 5xx, timeout). The
/// <see cref="ResumableUploadDriver"/> retries these with backoff up to the configured
/// attempt ceiling; any other exception type is treated as fatal and propagated.
/// </summary>
public sealed class ChunkUploadException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The error message.</param>
    public ChunkUploadException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="inner">The underlying transport error.</param>
    public ChunkUploadException(string message, Exception inner) : base(message, inner)
    {
    }
}
