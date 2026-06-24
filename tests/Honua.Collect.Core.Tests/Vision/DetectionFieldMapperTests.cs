using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Vision;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Vision;

public class DetectionFieldMapperTests
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
                    new FormField { FieldId = "assetType", Label = "Asset", Type = FormFieldType.Text },
                    new FormField { FieldId = "poleCount", Label = "Poles", Type = FormFieldType.Numeric },
                ],
            },
        ],
    };

    private static DetectionFieldMapper ProMapper() => new(new CollectEntitlements(CollectEdition.Pro));

    private static DetectionResult Result(params Detection[] detections)
        => new(detections, 1000, 1000);

    private static Detection Det(string label, double confidence)
        => new(label, confidence, new NormalizedBoundingBox(0.4, 0.4, 0.1, 0.1));

    [Fact]
    public void Community_edition_is_not_entitled()
    {
        var mapper = new DetectionFieldMapper(CollectEntitlements.Community);
        Assert.False(mapper.IsAvailable);

        var session = FormSession.CreateForNewRecord(Form(), "r1");
        Assert.Throws<FeatureNotEntitledException>(() => mapper.Apply(
            session,
            Result(Det("utility-pole", 0.9)),
            [new DetectionFieldRule("assetType", DetectionFieldKind.Category)]));
    }

    [Fact]
    public void Maps_highest_confidence_label_to_a_category_field()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var outcome = ProMapper().Apply(
            session,
            Result(Det("sign", 0.55), Det("utility-pole", 0.91)),
            [new DetectionFieldRule("assetType", DetectionFieldKind.Category)]);

        Assert.Equal(["assetType"], outcome.Applied);
        Assert.Equal("utility-pole", session.GetValue("assetType"));
    }

    [Fact]
    public void Maps_matching_detection_count_to_a_numeric_field()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var outcome = ProMapper().Apply(
            session,
            Result(Det("utility-pole", 0.9), Det("utility-pole", 0.8), Det("sign", 0.95)),
            [new DetectionFieldRule("poleCount", DetectionFieldKind.Count) { Label = "utility-pole" }]);

        Assert.Equal(["poleCount"], outcome.Applied);
        Assert.Equal(2, session.GetValue("poleCount"));
    }

    [Fact]
    public void Low_confidence_detections_are_excluded_from_count_and_category()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var outcome = ProMapper().Apply(
            session,
            // One below the 0.5 default threshold; only one pole should count.
            Result(Det("utility-pole", 0.9), Det("utility-pole", 0.30)),
            [new DetectionFieldRule("poleCount", DetectionFieldKind.Count) { Label = "utility-pole" }]);

        Assert.Equal(1, session.GetValue("poleCount"));
        _ = outcome;
    }

    [Fact]
    public void Below_threshold_with_no_signal_skips_rather_than_writes_zero()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var outcome = ProMapper().Apply(
            session,
            Result(Det("utility-pole", 0.2)), // nothing clears the threshold
            [new DetectionFieldRule("poleCount", DetectionFieldKind.Count)]);

        Assert.Empty(outcome.Applied);
        Assert.Contains(outcome.Skipped, s => s.FieldId == "poleCount" && s.Reason == DetectionSkipReason.BelowThreshold);
        Assert.Null(session.GetValue("poleCount"));
    }

    [Fact]
    public void Unknown_fields_are_skipped()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var outcome = ProMapper().Apply(
            session,
            Result(Det("x", 0.9)),
            [new DetectionFieldRule("ghost", DetectionFieldKind.Category)]);

        Assert.Empty(outcome.Applied);
        Assert.Contains(outcome.Skipped, s => s.FieldId == "ghost" && s.Reason == DetectionSkipReason.UnknownField);
    }

    [Fact]
    public void Existing_user_values_are_never_overwritten_unless_requested()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        session.SetValue("assetType", "manhole"); // operator already answered

        var rule = new[] { new DetectionFieldRule("assetType", DetectionFieldKind.Category) };
        var result = Result(Det("utility-pole", 0.99));

        var kept = ProMapper().Apply(session, result, rule);
        Assert.Contains(kept.Skipped, s => s.FieldId == "assetType" && s.Reason == DetectionSkipReason.AlreadyFilled);
        Assert.Equal("manhole", session.GetValue("assetType"));

        var forced = ProMapper().Apply(session, result, rule, new DetectionMappingOptions { OverwriteExisting = true });
        Assert.Equal(["assetType"], forced.Applied);
        Assert.Equal("utility-pole", session.GetValue("assetType"));
    }

    [Fact]
    public void Category_rule_with_no_matching_label_reports_no_match()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var outcome = ProMapper().Apply(
            session,
            Result(Det("sign", 0.9)),
            [new DetectionFieldRule("assetType", DetectionFieldKind.Category) { Label = "utility-pole" }]);

        Assert.Empty(outcome.Applied);
        Assert.Contains(outcome.Skipped, s => s.FieldId == "assetType" && s.Reason == DetectionSkipReason.NoMatch);
    }
}
