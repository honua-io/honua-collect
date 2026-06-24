using System.Security.Cryptography;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// The retry/backoff policy for a <see cref="ResumableUploadDriver"/>: how many times a
/// transient <see cref="ChunkUploadException"/> is retried and the (deterministic,
/// exponential) delay between attempts. Capped so a flaky link does not back off forever.
/// </summary>
/// <param name="MaxAttemptsPerChunk">Total attempts per chunk (1 = no retry). Must be >= 1.</param>
/// <param name="BaseDelay">The first retry delay; subsequent delays double up to <paramref name="MaxDelay"/>.</param>
/// <param name="MaxDelay">The ceiling on a single backoff delay.</param>
public readonly record struct ChunkRetryPolicy(int MaxAttemptsPerChunk, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    /// <summary>A sensible default: 5 attempts, 200 ms base, 5 s ceiling.</summary>
    public static ChunkRetryPolicy Default { get; } =
        new(MaxAttemptsPerChunk: 5, BaseDelay: TimeSpan.FromMilliseconds(200), MaxDelay: TimeSpan.FromSeconds(5));

    /// <summary>The backoff delay before the given (1-based) retry number.</summary>
    /// <param name="retryNumber">The retry ordinal (1 for the first retry).</param>
    /// <returns>The delay, clamped to <see cref="MaxDelay"/>.</returns>
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

/// <summary>
/// Drives a <see cref="ResumableUpload"/> to completion against an <see cref="IChunkSink"/>:
/// it walks the pending chunks (resuming from wherever a prior run left off), reads each
/// chunk's bytes from the in-memory file, computes the per-chunk SHA-256, pushes it through
/// the sink with retry/backoff, records progress, and finally verifies whole-file integrity.
/// Platform-neutral and fully unit-testable with a fake sink — no live network.
/// </summary>
public sealed class ResumableUploadDriver
{
    private readonly IChunkSink _sink;
    private readonly ChunkRetryPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates a driver.</summary>
    /// <param name="sink">The chunk transport.</param>
    /// <param name="policy">The retry/backoff policy (defaults to <see cref="ChunkRetryPolicy.Default"/>).</param>
    /// <param name="delay">
    /// The backoff sleep function, overridable so tests run without real waits. Defaults to
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </param>
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

    /// <summary>
    /// Uploads every pending chunk of <paramref name="upload"/> from <paramref name="content"/>,
    /// then verifies the whole-file digest.
    /// </summary>
    /// <param name="upload">The plan/tracker; already-completed chunks (a resume) are skipped.</param>
    /// <param name="content">The full file bytes (length must equal <see cref="ResumableUpload.TotalBytes"/>).</param>
    /// <param name="expectedSha256Hex">The expected lowercase-hex SHA-256 of the whole file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upload result, including whether final integrity passed.</returns>
    /// <exception cref="ArgumentException">If <paramref name="content"/> length != the planned total.</exception>
    /// <exception cref="ChunkUploadException">If a chunk exhausts its retry budget.</exception>
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

/// <summary>The result of a driven resumable upload.</summary>
/// <param name="ChunkCount">Total chunks in the file.</param>
/// <param name="TransportAttempts">
/// Total sink send-attempts made by this run, including retries (a clean run with no
/// retries equals the number of chunks that still needed uploading).
/// </param>
/// <param name="IntegrityVerified">Whether the final whole-file digest matched.</param>
public readonly record struct ResumableUploadOutcome(int ChunkCount, int TransportAttempts, bool IntegrityVerified);
