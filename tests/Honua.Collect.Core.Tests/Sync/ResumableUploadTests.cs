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
}
