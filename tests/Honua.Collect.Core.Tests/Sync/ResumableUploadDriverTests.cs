using System.Security.Cryptography;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

public class ResumableUploadDriverTests
{
    private static string Sha256Hex(byte[] content)
        => Convert.ToHexStringLower(SHA256.HashData(content));

    // No real backoff sleeps in tests.
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    /// <summary>An in-memory chunk sink that assembles the file and can be told to fail.</summary>
    private sealed class FakeSink : IChunkSink
    {
        private readonly Func<UploadChunk, int, bool>? _failPredicate;
        private readonly Dictionary<int, int> _attemptsByChunk = [];

        public FakeSink(Func<UploadChunk, int, bool>? failPredicate = null) => _failPredicate = failPredicate;

        public List<UploadChunk> Received { get; } = [];
        public Dictionary<long, byte[]> ByOffset { get; } = [];
        public List<string> ReceivedHashes { get; } = [];

        public Task SendAsync(UploadChunk chunk, ReadOnlyMemory<byte> bytes, string chunkSha256Hex, CancellationToken cancellationToken)
        {
            var attempt = _attemptsByChunk.GetValueOrDefault(chunk.Index) + 1;
            _attemptsByChunk[chunk.Index] = attempt;

            if (_failPredicate is not null && _failPredicate(chunk, attempt))
            {
                throw new ChunkUploadException($"transient failure on chunk {chunk.Index} attempt {attempt}");
            }

            // Verify the driver-supplied hash matches the bytes it handed us.
            Assert.Equal(Sha256Hex(bytes.ToArray()), chunkSha256Hex);

            Received.Add(chunk);
            ReceivedHashes.Add(chunkSha256Hex);
            ByOffset[chunk.Offset] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public byte[] Assemble(long totalBytes)
        {
            var buffer = new byte[totalBytes];
            foreach (var (offset, bytes) in ByOffset)
            {
                bytes.CopyTo(buffer, (int)offset);
            }

            return buffer;
        }
    }

    [Fact]
    public async Task Uploads_all_chunks_and_verifies_final_integrity()
    {
        var content = Enumerable.Range(0, 250).Select(i => (byte)i).ToArray();
        var upload = new ResumableUpload(content.Length, chunkSize: 100);
        var sink = new FakeSink();
        var driver = new ResumableUploadDriver(sink, delay: NoDelay);

        var outcome = await driver.UploadAsync(upload, content, Sha256Hex(content));

        Assert.True(upload.IsComplete);
        Assert.Equal(ResumableUploadState.Completed, upload.State);
        Assert.True(outcome.IntegrityVerified);
        Assert.Equal(3, outcome.ChunkCount);
        Assert.Equal(3, outcome.TransportAttempts); // clean run: one attempt per chunk
        // The reassembled file is byte-identical and in order.
        Assert.Equal(content, sink.Assemble(content.Length));
        Assert.Equal([0, 1, 2], sink.Received.Select(c => c.Index));
    }

    [Fact]
    public async Task Last_chunk_is_a_short_remainder()
    {
        var content = new byte[250];
        var upload = new ResumableUpload(content.Length, chunkSize: 100);
        var sink = new FakeSink();
        var driver = new ResumableUploadDriver(sink, delay: NoDelay);

        await driver.UploadAsync(upload, content, Sha256Hex(content));

        Assert.Equal(50, sink.Received.Single(c => c.Index == 2).Length);
    }

    [Fact]
    public async Task Resumes_after_interruption_at_an_arbitrary_offset()
    {
        var content = Enumerable.Range(0, 500).Select(i => (byte)(i % 251)).ToArray();
        var expected = Sha256Hex(content);

        // First run: the sink hard-fails chunk index 2 (a non-retryable fatal to stop mid-stream).
        var upload = new ResumableUpload(content.Length, chunkSize: 100); // 5 chunks
        var failingSink = new FakeSinkThatThrowsFatal(failAtIndex: 2);
        var driver = new ResumableUploadDriver(failingSink, delay: NoDelay);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.UploadAsync(upload, content, expected));

        // Chunks 0 and 1 made it; 2,3,4 did not — that's the resume cursor.
        Assert.Equal(2, upload.CompletedCount);
        Assert.Equal(ResumableUploadState.InProgress, upload.State);
        Assert.Equal([2, 3, 4], upload.PendingChunks().Select(c => c.Index));

        // Second run with a healthy sink completes from offset 200 onward.
        var goodSink = new FakeSink();
        var resumeDriver = new ResumableUploadDriver(goodSink, delay: NoDelay);
        var outcome = await resumeDriver.UploadAsync(upload, content, expected);

        Assert.True(upload.IsComplete);
        Assert.True(outcome.IntegrityVerified);
        // Only the 3 remaining chunks were sent on resume.
        Assert.Equal([2, 3, 4], goodSink.Received.Select(c => c.Index));
        Assert.Equal(3, outcome.TransportAttempts);
    }

    [Fact]
    public async Task Retries_transient_failures_with_backoff_then_succeeds()
    {
        var content = new byte[300];
        var upload = new ResumableUpload(content.Length, chunkSize: 100); // 3 chunks
        // Chunk 1 fails its first two attempts, succeeds on the third.
        var sink = new FakeSink(failPredicate: (chunk, attempt) => chunk.Index == 1 && attempt <= 2);
        var policy = new ChunkRetryPolicy(MaxAttemptsPerChunk: 5, BaseDelay: TimeSpan.FromMilliseconds(1), MaxDelay: TimeSpan.FromMilliseconds(10));

        var delays = new List<TimeSpan>();
        var driver = new ResumableUploadDriver(sink, policy, delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var outcome = await driver.UploadAsync(upload, content, Sha256Hex(content));

        Assert.True(outcome.IntegrityVerified);
        Assert.True(upload.IsComplete);
        // 3 chunks + 2 retries on chunk 1 = 5 attempts total.
        Assert.Equal(5, outcome.TransportAttempts);
        // Two backoff waits, exponential: base*2^0 then base*2^1.
        Assert.Equal(2, delays.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1), delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(2), delays[1]);
    }

    [Fact]
    public async Task Exhausting_the_retry_budget_throws_and_leaves_the_chunk_pending()
    {
        var content = new byte[200];
        var upload = new ResumableUpload(content.Length, chunkSize: 100); // 2 chunks
        var sink = new FakeSink(failPredicate: (chunk, _) => chunk.Index == 0); // chunk 0 always fails
        var policy = new ChunkRetryPolicy(MaxAttemptsPerChunk: 3, BaseDelay: TimeSpan.FromMilliseconds(1), MaxDelay: TimeSpan.FromMilliseconds(1));
        var driver = new ResumableUploadDriver(sink, policy, delay: NoDelay);

        await Assert.ThrowsAsync<ChunkUploadException>(
            () => driver.UploadAsync(upload, content, Sha256Hex(content)));

        Assert.False(upload.IsChunkComplete(0));
        Assert.Equal(0, upload.CompletedCount); // nothing committed
    }

    [Fact]
    public async Task Final_integrity_fails_on_a_wrong_expected_digest()
    {
        var content = new byte[150];
        var upload = new ResumableUpload(content.Length, chunkSize: 100);
        var sink = new FakeSink();
        var driver = new ResumableUploadDriver(sink, delay: NoDelay);

        var outcome = await driver.UploadAsync(upload, content, expectedSha256Hex: new string('a', 64));

        Assert.True(upload.IsComplete);
        Assert.False(outcome.IntegrityVerified); // assembled bytes don't hash to the bogus digest
    }

    [Fact]
    public async Task Content_length_mismatch_is_rejected()
    {
        var upload = new ResumableUpload(totalBytes: 100, chunkSize: 50);
        var driver = new ResumableUploadDriver(new FakeSink(), delay: NoDelay);

        await Assert.ThrowsAsync<ArgumentException>(
            () => driver.UploadAsync(upload, new byte[99], Sha256Hex(new byte[99])));
    }

    /// <summary>A sink that throws a FATAL (non-retryable) exception at a given chunk index.</summary>
    private sealed class FakeSinkThatThrowsFatal : IChunkSink
    {
        private readonly int _failAtIndex;

        public FakeSinkThatThrowsFatal(int failAtIndex) => _failAtIndex = failAtIndex;

        public Task SendAsync(UploadChunk chunk, ReadOnlyMemory<byte> bytes, string chunkSha256Hex, CancellationToken cancellationToken)
        {
            if (chunk.Index == _failAtIndex)
            {
                // Not a ChunkUploadException -> the driver treats it as fatal and propagates.
                throw new InvalidOperationException($"fatal at chunk {chunk.Index}");
            }

            return Task.CompletedTask;
        }
    }
}
