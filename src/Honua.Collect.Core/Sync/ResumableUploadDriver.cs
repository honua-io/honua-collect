// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace Honua.Collect.Core.Sync;

public readonly record struct ChunkRetryPolicy(int MaxAttemptsPerChunk, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    public static ChunkRetryPolicy Default { get; } =
        new(MaxAttemptsPerChunk: 5, BaseDelay: TimeSpan.FromMilliseconds(200), MaxDelay: TimeSpan.FromSeconds(5));

    public TimeSpan DelayBeforeRetry(int retryNumber)
    {
        if (retryNumber <= 0)
        {
            return TimeSpan.Zero;
        }

        // Exponential: base * 2^(retryNumber-1), clamped. Computed in ticks to avoid overflow.
        var factor = Math.Pow(2, retryNumber - 1);
        var ticks = BaseDelay.Ticks * factor;
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
    }
}

public sealed class ResumableUploadDriver
{
    private readonly IChunkSink _sink;
    private readonly ChunkRetryPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ResumableUploadDriver(
        IChunkSink sink,
        ChunkRetryPolicy? policy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _policy = policy ?? ChunkRetryPolicy.Default;
        if (_policy.MaxAttemptsPerChunk < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "MaxAttemptsPerChunk must be at least 1.");
        }

        _delay = delay ?? Task.Delay;
    }

    public async Task<ResumableUploadOutcome> UploadAsync(
        ResumableUpload upload,
        byte[] content,
        string expectedSha256Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(expectedSha256Hex);
        if (content.LongLength != upload.TotalBytes)
        {
            throw new ArgumentException(
                $"Content length {content.LongLength} does not match the planned total {upload.TotalBytes}.",
                nameof(content));
        }

        var attemptsUsed = 0;

        while (upload.NextPending() is { } chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = new ReadOnlyMemory<byte>(content, (int)chunk.Offset, (int)chunk.Length);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));

            attemptsUsed += await SendWithRetryAsync(chunk, bytes, hash, cancellationToken)
                .ConfigureAwait(false);

            upload.MarkUploaded(chunk.Index, hash);
        }

        var integrityOk = upload.VerifyFinalIntegrity(content, expectedSha256Hex);
        return new ResumableUploadOutcome(upload.ChunkCount, attemptsUsed, integrityOk);
    }

    public async Task<ResumableUploadOutcome> UploadAsync(
        ResumableUpload upload,
        Stream content,
        string expectedSha256Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(expectedSha256Hex);
        if (!content.CanRead || !content.CanSeek)
        {
            throw new ArgumentException("The content stream must be readable and seekable.", nameof(content));
        }

        if (content.Length != upload.TotalBytes)
        {
            throw new ArgumentException(
                $"Content length {content.Length} does not match the planned total {upload.TotalBytes}.",
                nameof(content));
        }

        var attemptsUsed = 0;

        while (upload.NextPending() is { } chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read exactly this chunk's bytes from its offset; only one chunk is held
            // in memory at a time rather than the whole file.
            var buffer = new byte[chunk.Length];
            content.Position = chunk.Offset;
            await content.ReadExactlyAsync(buffer.AsMemory(0, (int)chunk.Length), cancellationToken).ConfigureAwait(false);

            var bytes = new ReadOnlyMemory<byte>(buffer);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));

            attemptsUsed += await SendWithRetryAsync(chunk, bytes, hash, cancellationToken)
                .ConfigureAwait(false);

            upload.MarkUploaded(chunk.Index, hash);
        }

        var integrityOk = upload.VerifyFinalIntegrity(content, expectedSha256Hex);
        return new ResumableUploadOutcome(upload.ChunkCount, attemptsUsed, integrityOk);
    }

    // Sends one chunk, retrying transient ChunkUploadExceptions with backoff. Returns the
    // number of attempts made (>=1). Throws once the attempt budget is exhausted.
    private async Task<int> SendWithRetryAsync(
        UploadChunk chunk,
        ReadOnlyMemory<byte> bytes,
        string hash,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _sink.SendAsync(chunk, bytes, hash, cancellationToken).ConfigureAwait(false);
                return attempt;
            }
            catch (ChunkUploadException) when (attempt < _policy.MaxAttemptsPerChunk)
            {
                var delay = _policy.DelayBeforeRetry(attempt);
                if (delay > TimeSpan.Zero)
                {
                    await _delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}

public readonly record struct ResumableUploadOutcome(int ChunkCount, int TransportAttempts, bool IntegrityVerified);
