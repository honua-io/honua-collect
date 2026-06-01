using Honua.Collect.Core.Field;

namespace Honua.Collect.Core.Tests.Field;

public class CapturedMediaAttachmentTests
{
    [Fact]
    public void ToSdkAttachment_strips_local_path_to_filename()
    {
        var captured = new CapturedMediaAttachment
        {
            AttachmentId = "a1",
            LocalPath = "/var/mobile/honua/photo-123.jpg",
            ContentType = "image/jpeg",
        };

        var sdk = captured.ToSdkAttachment();

        Assert.Equal("a1", sdk.AttachmentId);
        Assert.Equal("photo-123.jpg", sdk.FileName);
        Assert.Equal("image/jpeg", sdk.ContentType);
    }

    [Fact]
    public void WithEvidenceMetadata_merges_into_a_copy_without_mutating_the_original()
    {
        var first = new CapturedMediaAttachment { AttachmentId = "a1", LocalPath = "x.jpg" }
            .WithEvidenceMetadata(new Dictionary<string, object?> { ["anchor"] = "ar-1" });

        var second = first.WithEvidenceMetadata(new Dictionary<string, object?> { ["pose"] = 42 });

        Assert.Equal("ar-1", second.EvidenceMetadata["anchor"]);
        Assert.Equal(42, second.EvidenceMetadata["pose"]);
        Assert.False(first.EvidenceMetadata.ContainsKey("pose"));
    }
}
