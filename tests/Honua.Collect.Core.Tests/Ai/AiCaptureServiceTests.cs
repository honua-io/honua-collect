using Honua.Collect.Core.Ai;
using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Ai;

public class AiCaptureServiceTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "f",
        Name = "f",
        Sections =
        [
            new FormSection
            {
                SectionId = "s",
                Label = "s",
                Fields =
                [
                    new FormField { FieldId = "species", Label = "Species", Type = FormFieldType.Text },
                    new FormField { FieldId = "count", Label = "Count", Type = FormFieldType.Numeric },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
        ],
    };

    private static AiCaptureService ProService() => new(new CollectEntitlements(CollectEdition.Pro));

    [Fact]
    public void Community_edition_is_not_entitled_to_ai_capture()
    {
        var service = new AiCaptureService(CollectEntitlements.Community);
        Assert.False(service.IsAvailable);

        var session = FormSession.CreateForNewRecord(Form(), "r1");
        Assert.Throws<FeatureNotEntitledException>(
            () => service.Apply(session, new FieldExtractionResult { Fields = [new ExtractedField("species", "Oak", 0.9)] }));
    }

    [Fact]
    public void Applies_high_confidence_fields_and_skips_low_confidence()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var result = new FieldExtractionResult
        {
            Fields =
            [
                new ExtractedField("species", "Oak", 0.95),
                new ExtractedField("count", 3, 0.30), // below threshold
            ],
        };

        var outcome = ProService().Apply(session, result);

        Assert.Equal(["species"], outcome.Applied);
        Assert.Equal("Oak", session.GetValue("species"));
        Assert.Contains(outcome.Skipped, s => s.FieldId == "count" && s.Reason == AiSkipReason.LowConfidence);
    }

    [Fact]
    public void Unknown_fields_are_skipped()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var outcome = ProService().Apply(session,
            new FieldExtractionResult { Fields = [new ExtractedField("ghost", "x", 0.99)] });

        Assert.Empty(outcome.Applied);
        Assert.Contains(outcome.Skipped, s => s.FieldId == "ghost" && s.Reason == AiSkipReason.UnknownField);
    }

    [Fact]
    public void Existing_values_are_preserved_unless_overwrite_requested()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        session.SetValue("species", "Maple");

        var result = new FieldExtractionResult { Fields = [new ExtractedField("species", "Oak", 0.99)] };

        var kept = ProService().Apply(session, result);
        Assert.Contains(kept.Skipped, s => s.FieldId == "species" && s.Reason == AiSkipReason.AlreadyFilled);
        Assert.Equal("Maple", session.GetValue("species"));

        var overwritten = ProService().Apply(session, result, new AiApplyOptions { OverwriteExisting = true });
        Assert.Equal(["species"], overwritten.Applied);
        Assert.Equal("Oak", session.GetValue("species"));
    }

    [Fact]
    public void Redaction_planner_lists_only_attachments_requiring_blur()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        session.AddMedia(new CapturedMediaAttachment { AttachmentId = "a1", FieldId = "photo", LocalPath = "/a.jpg", RequiresFaceBlur = true });
        session.AddMedia(new CapturedMediaAttachment { AttachmentId = "a2", FieldId = "photo", LocalPath = "/b.jpg", RequiresFaceBlur = false });

        var toRedact = MediaRedactionPlanner.AttachmentsRequiringRedaction(session);

        Assert.Single(toRedact);
        Assert.Equal("a1", toRedact[0].AttachmentId);
    }
}
