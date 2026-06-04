using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

public class ResumableUploadTests
{
    [Fact]
    public void Chunk_count_and_last_chunk_length_are_correct()
    {
        var upload = new ResumableUpload(totalBytes: 250, chunkSize: 100);

        Assert.Equal(3, upload.ChunkCount);
        Assert.Equal(new UploadChunk(0, 0, 100), upload.ChunkAt(0));
        Assert.Equal(new UploadChunk(2, 200, 50), upload.ChunkAt(2)); // remainder
    }

    [Fact]
    public void Progress_advances_as_chunks_complete()
    {
        var upload = new ResumableUpload(300, 100);
        Assert.Equal(0, upload.Progress, 6);

        upload.MarkUploaded(0);
        upload.MarkUploaded(1);
        Assert.Equal(2.0 / 3.0, upload.Progress, 6);
        Assert.False(upload.IsComplete);

        upload.MarkUploaded(2);
        Assert.True(upload.IsComplete);
        Assert.Equal(1.0, upload.Progress, 6);
    }

    [Fact]
    public void Pending_chunks_are_the_resume_work_list()
    {
        var upload = new ResumableUpload(300, 100);
        upload.MarkUploaded(1);

        var pending = upload.PendingChunks();
        Assert.Equal([0, 2], pending.Select(c => c.Index));
    }

    [Fact]
    public void Resume_restores_completed_chunks_from_persistence()
    {
        var upload = new ResumableUpload(500, 100);
        upload.Resume([0, 1, 99]); // 99 is out of range and ignored

        Assert.Equal(2, upload.CompletedCount);
        Assert.Equal([2, 3, 4], upload.PendingChunks().Select(c => c.Index));
    }

    [Fact]
    public void Empty_file_is_immediately_complete()
    {
        var upload = new ResumableUpload(0, 100);
        Assert.Equal(0, upload.ChunkCount);
        Assert.True(upload.IsComplete);
        Assert.Equal(1.0, upload.Progress, 6);
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResumableUpload(100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResumableUpload(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResumableUpload(100, 100).ChunkAt(5));
    }

    [Fact]
    public void Chunk_index_boundary_is_exclusive_of_ChunkCount()
    {
        // ChunkCount == 3 (indices 0..2); index == ChunkCount must be rejected,
        // pinning the >= boundary against an off-by-one mutation.
        var upload = new ResumableUpload(250, 100);
        Assert.Equal(3, upload.ChunkCount);

        Assert.Throws<ArgumentOutOfRangeException>(() => upload.ChunkAt(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => upload.MarkUploaded(3));
        // The last valid index does NOT throw.
        var lastChunk = upload.ChunkAt(2);
        Assert.Equal(2, lastChunk.Index);
        upload.MarkUploaded(2);
        Assert.Equal(1, upload.CompletedCount);
    }

    [Fact]
    public void Resume_ignores_index_equal_to_ChunkCount_but_accepts_the_last_valid_index()
    {
        // ChunkCount == 2 (indices 0,1). Resume with the last valid index (1) AND
        // the first out-of-range index (2): only index 1 is restored, killing the
        // index < ChunkCount boundary mutation.
        var upload = new ResumableUpload(200, 100);
        Assert.Equal(2, upload.ChunkCount);

        upload.Resume([1, 2]);

        Assert.Equal(1, upload.CompletedCount);
        var pending = upload.PendingChunks();
        Assert.Single(pending); // only chunk 0 remains pending
        Assert.Equal(0, pending[0].Index);
    }
}
