using Honua.Collect.Core.Ai.OnDevice;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Ai.OnDevice;

public class OnDeviceAiCaptureTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "inspection",
        Name = "Tree Inspection",
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
                    new FormField { FieldId = "alive", Label = "Alive", Type = FormFieldType.YesNo },
                    new FormField
                    {
                        FieldId = "health",
                        Label = "Health",
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "good", Label = "Good" },
                            new FieldChoice { Value = "poor", Label = "Poor" },
                        ],
                    },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
        ],
    };

    private static FormSession Session() => FormSession.CreateForNewRecord(Form(), "r1");

    // ---- Rule-based text extraction ----

    [Fact]
    public void Text_extractor_maps_keyword_value_to_typed_fields()
    {
        var result = RuleBasedTextFieldExtractor.Extract(
            "Species: white oak. Count is 3. Alive yes. Health good.",
            Form());

        var species = result.Suggestions.Single(s => s.FieldId == "species");
        Assert.Equal("white oak", species.Value);

        var count = result.Suggestions.Single(s => s.FieldId == "count");
        Assert.Equal(3d, count.Value);

        var alive = result.Suggestions.Single(s => s.FieldId == "alive");
        Assert.Equal(true, alive.Value);

        var health = result.Suggestions.Single(s => s.FieldId == "health");
        Assert.Equal("good", health.Value); // matched the choice value
    }

    [Fact]
    public void Text_extractor_matches_choice_by_label_and_returns_choice_value()
    {
        var result = RuleBasedTextFieldExtractor.Extract("Health Poor", Form());
        var health = result.Suggestions.Single(s => s.FieldId == "health");
        Assert.Equal("poor", health.Value);
    }

    [Fact]
    public void Text_extractor_returns_unmapped_when_nothing_matches()
    {
        var result = RuleBasedTextFieldExtractor.Extract("the weather is nice today", Form());
        Assert.Empty(result.Suggestions);
        Assert.False(string.IsNullOrEmpty(result.Unmapped));
    }

    [Fact]
    public void Text_extractor_humanizes_camel_case_field_id_as_keyword()
    {
        var form = new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections =
            [
                new FormSection
                {
                    SectionId = "s",
                    Label = "s",
                    Fields = [new FormField { FieldId = "treeHeight", Label = "treeHeight", Type = FormFieldType.Numeric }],
                },
            ],
        };

        var result = RuleBasedTextFieldExtractor.Extract("tree height 12", form);
        Assert.Equal(12d, result.Suggestions.Single().Value);
    }

    // ---- Stub provider contract ----

    [Fact]
    public async Task Stub_provider_is_offline_and_returns_empty_for_audio_and_photo()
    {
        var stub = StubOnDeviceAiCapture.Instance;
        Assert.True(stub.SupportsOffline);
        Assert.Equal("stub", stub.EngineId);

        var transcription = await stub.TranscribeAudioAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(AiTranscription.Empty, transcription);

        var photo = await stub.ExtractFieldsFromPhotoAsync(new byte[] { 1, 2, 3 }, Form());
        Assert.Empty(photo.Suggestions);
    }

    [Fact]
    public void Stub_provider_text_extraction_delegates_to_rule_based_extractor()
    {
        var result = StubOnDeviceAiCapture.Instance.ExtractFieldsFromText("Count: 7", Form());
        Assert.Equal(7d, result.Suggestions.Single(s => s.FieldId == "count").Value);
    }

    [Fact]
    public async Task Stub_provider_honors_cancellation()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => StubOnDeviceAiCapture.Instance.TranscribeAudioAsync(new byte[] { 1 }, cts.Token));
    }

    // ---- Suggestion -> session mapping (the safe core) ----

    [Fact]
    public void Mapper_coerces_value_to_field_type()
    {
        var session = Session();
        var suggestions = new AiFieldSuggestions
        {
            Suggestions = [new AiFieldSuggestion("count", "3", 0.9)],
        };

        var set = AiSuggestionMapper.Map(session, suggestions);
        var count = set.Suggestions.Single();
        Assert.Equal(AiSuggestionStatus.Ready, count.Status);
        Assert.Equal(3L, count.CoercedValue); // "3" coerced to numeric
    }

    [Fact]
    public void Mapper_flags_low_confidence_without_applying()
    {
        var session = Session();
        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions { Suggestions = [new AiFieldSuggestion("species", "Oak", 0.2)] });

        var s = set.Suggestions.Single();
        Assert.Equal(AiSuggestionStatus.LowConfidence, s.Status);
        Assert.False(s.IsApplyable);

        AiSuggestionMapper.Apply(session, set);
        Assert.Null(session.GetValue("species"));
    }

    [Fact]
    public void Mapper_rejects_uncoercible_value_as_invalid()
    {
        var session = Session();
        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions { Suggestions = [new AiFieldSuggestion("count", "not a number", 0.95)] });

        Assert.Equal(AiSuggestionStatus.InvalidValue, set.Suggestions.Single().Status);
    }

    [Fact]
    public void Mapper_rejects_value_outside_numeric_validation_range()
    {
        var form = new FormDefinition
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
                        new FormField
                        {
                            FieldId = "count",
                            Label = "Count",
                            Type = FormFieldType.Numeric,
                            Validation = new FieldValidationRule { MinNumericValue = 0, MaxNumericValue = 10 },
                        },
                    ],
                },
            ],
        };
        var session = FormSession.CreateForNewRecord(form, "r1");

        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions { Suggestions = [new AiFieldSuggestion("count", "999", 0.95)] });

        Assert.Equal(AiSuggestionStatus.InvalidValue, set.Suggestions.Single().Status);
    }

    [Fact]
    public void Mapper_rejects_choice_value_not_in_options()
    {
        var session = Session();
        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions { Suggestions = [new AiFieldSuggestion("health", "excellent", 0.95)] });

        Assert.Equal(AiSuggestionStatus.InvalidValue, set.Suggestions.Single().Status);
    }

    [Fact]
    public void Mapper_never_overwrites_a_user_entered_value()
    {
        var session = Session();
        session.SetValue("species", "Maple"); // user typed this

        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions { Suggestions = [new AiFieldSuggestion("species", "Oak", 0.99)] });

        var s = set.Suggestions.Single();
        Assert.Equal(AiSuggestionStatus.AlreadyFilled, s.Status);
        Assert.False(s.IsApplyable);

        AiSuggestionMapper.Apply(session, set);
        Assert.Equal("Maple", session.GetValue("species")); // preserved
    }

    [Fact]
    public void Mapper_overwrites_only_when_explicitly_requested()
    {
        var session = Session();
        session.SetValue("species", "Maple");

        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions { Suggestions = [new AiFieldSuggestion("species", "Oak", 0.99)] },
            new Honua.Collect.Core.Ai.AiApplyOptions { OverwriteExisting = true });

        Assert.Equal(AiSuggestionStatus.Ready, set.Suggestions.Single().Status);
        AiSuggestionMapper.Apply(session, set);
        Assert.Equal("Oak", session.GetValue("species"));
    }

    [Fact]
    public void Mapper_flags_unknown_field()
    {
        var session = Session();
        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions { Suggestions = [new AiFieldSuggestion("ghost", "x", 0.99)] });

        Assert.Equal(AiSuggestionStatus.UnknownField, set.Suggestions.Single().Status);
    }

    [Fact]
    public void Apply_writes_only_accepted_ready_suggestions()
    {
        var session = Session();
        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions
            {
                Suggestions =
                [
                    new AiFieldSuggestion("species", "Oak", 0.95),   // ready
                    new AiFieldSuggestion("count", "5", 0.2),        // low confidence
                ],
            });

        // Ready ones default Accepted=true; flagged ones default false.
        var result = AiSuggestionMapper.Apply(session, set);

        Assert.Equal(["species"], result.AppliedFieldIds);
        Assert.Equal("Oak", session.GetValue("species"));
        Assert.Null(session.GetValue("count"));
    }

    [Fact]
    public void Apply_respects_user_rejecting_a_ready_suggestion()
    {
        var session = Session();
        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions { Suggestions = [new AiFieldSuggestion("species", "Oak", 0.95)] });

        // User unchecks the suggestion in the review UI.
        var rejected = set with
        {
            Suggestions = set.Suggestions.Select(s => s with { Accepted = false }).ToList(),
        };

        var result = AiSuggestionMapper.Apply(session, rejected);
        Assert.Empty(result.AppliedFieldIds);
        Assert.Null(session.GetValue("species"));
    }

    [Fact]
    public void Ready_and_flagged_partition_the_set()
    {
        var session = Session();
        var set = AiSuggestionMapper.Map(
            session,
            new AiFieldSuggestions
            {
                Suggestions =
                [
                    new AiFieldSuggestion("species", "Oak", 0.95),
                    new AiFieldSuggestion("count", "5", 0.1),
                    new AiFieldSuggestion("ghost", "x", 0.95),
                ],
            });

        Assert.Single(set.Ready);
        Assert.Equal(2, set.Flagged.Count());
    }

    [Fact]
    public void End_to_end_transcript_to_session_via_stub_and_mapper()
    {
        var session = Session();
        var suggestions = StubOnDeviceAiCapture.Instance.ExtractFieldsFromText(
            "Species: red maple. Count 4. Health good.",
            Form());

        var set = AiSuggestionMapper.Map(session, suggestions);
        AiSuggestionMapper.Apply(session, set);

        Assert.Equal("red maple", session.GetValue("species"));
        Assert.Equal(4L, session.GetValue("count"));
        Assert.Equal("good", session.GetValue("health"));
    }
}
