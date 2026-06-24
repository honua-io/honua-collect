using Honua.Collect.Core.Automation;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>
/// Tests for the #44 Data Events engine increment: expression-based conditions and
/// computed values reusing the SDK form-expression engine, the new offline actions
/// (tag / notification / follow-up), the cascade loop guard, deterministic
/// ordering, and the AI-action seam (stubbed — no network).
/// </summary>
public class AutomationEngineTests
{
    private static Dictionary<string, object?> Values(params (string Id, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal);

    private readonly AutomationRuntime _runtime = new();

    // ----- Expression conditions (reuse the SDK form-expression engine) -----

    [Fact]
    public void Expression_condition_gates_the_action_using_the_sdk_engine()
    {
        var rule = new AutomationRule
        {
            Name = "high-severity",
            Trigger = AutomationTrigger.RecordSave,
            // Same language as field visibility/relevance.
            Condition = new ExpressionCondition("${severity} = 'high' and ${score} > 5"),
            Actions = [new SetFieldAction("escalate", true)],
        };

        var fires = _runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordSave), Values(("severity", "high"), ("score", 9)));
        Assert.Equal(true, fires.Values["escalate"]);

        var skips = _runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordSave), Values(("severity", "high"), ("score", 2)));
        Assert.Empty(skips.FiredRules);
    }

    // ----- Computed-value action (reuse the SDK expression engine) -----

    [Fact]
    public void Compute_action_sets_a_field_from_an_expression()
    {
        var rule = new AutomationRule
        {
            Name = "area",
            Trigger = AutomationTrigger.FieldChanged,
            Actions = [new ComputeFieldAction("area", "${length} * ${width}")],
        };

        var result = _runtime.Run(
            [rule],
            new AutomationEvent(AutomationTrigger.FieldChanged, "width"),
            Values(("length", 4), ("width", 3)));

        Assert.Equal(12d, Convert.ToDouble(result.Values["area"]));
    }

    // ----- New offline actions -----

    [Fact]
    public void Tag_notification_and_followup_actions_accumulate_offline()
    {
        var rule = new AutomationRule
        {
            Name = "flag",
            Trigger = AutomationTrigger.RecordSave,
            Actions =
            [
                new AddTagAction("needs-review"),
                new AddTagAction("needs-review"), // duplicate is ignored
                new EnqueueNotificationAction("Review", "A record needs review."),
                new ScheduleFollowUpAction("Re-inspect site", 7),
            ],
        };

        var result = _runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordSave), Values());

        Assert.Equal(["needs-review"], result.Tags);
        var note = Assert.Single(result.Notifications);
        Assert.Equal("Review", note.Title);
        var followUp = Assert.Single(result.FollowUps);
        Assert.Equal(7, followUp.DueInDays);
    }

    // ----- Cascade + deterministic ordering -----

    [Fact]
    public void Field_change_cascades_into_dependent_rules_in_dependency_order()
    {
        var rules = new[]
        {
            // status change -> set grade
            new AutomationRule
            {
                Name = "on-status",
                Trigger = AutomationTrigger.FieldChanged,
                TriggerFieldId = "status",
                Actions = [new SetFieldAction("grade", "F")],
            },
            // grade change -> flag review (only reachable via cascade)
            new AutomationRule
            {
                Name = "on-grade",
                Trigger = AutomationTrigger.FieldChanged,
                TriggerFieldId = "grade",
                Actions = [new SetFieldAction("needsReview", true)],
            },
        };

        var result = _runtime.Run(rules, new AutomationEvent(AutomationTrigger.FieldChanged, "status"), Values(("status", "closed")));

        Assert.Equal("F", result.Values["grade"]);
        Assert.Equal(true, result.Values["needsReview"]);
        // on-status fires first (initial wave), then on-grade (cascade wave).
        Assert.Equal(["on-status", "on-grade"], result.FiredRules);
    }

    // ----- Loop guard -----

    [Fact]
    public void Loop_guard_terminates_a_mutually_retriggering_pair()
    {
        // a's change sets b; b's change sets a back — an infinite ping-pong absent a guard.
        var rules = new[]
        {
            new AutomationRule
            {
                Name = "a->b",
                Trigger = AutomationTrigger.FieldChanged,
                TriggerFieldId = "a",
                Actions = [new SetFieldAction("b", "from-a")],
            },
            new AutomationRule
            {
                Name = "b->a",
                Trigger = AutomationTrigger.FieldChanged,
                TriggerFieldId = "b",
                Actions = [new SetFieldAction("a", "from-b")],
            },
        };

        // Must return (not hang). Each field cascades at most once.
        var result = _runtime.Run(rules, new AutomationEvent(AutomationTrigger.FieldChanged, "a"), Values(("a", "seed")));

        Assert.Equal("from-a", result.Values["b"]);
        Assert.Equal("from-b", result.Values["a"]);
        // a (initial) -> b cascade -> a cascade; a is not re-cascaded a second time.
        Assert.Equal(["a->b", "b->a"], result.FiredRules);
    }

    [Fact]
    public void Setting_a_field_to_its_current_value_does_not_cascade()
    {
        var rule = new AutomationRule
        {
            Name = "noop-set",
            Trigger = AutomationTrigger.FieldChanged,
            TriggerFieldId = "x",
            Actions = [new SetFieldAction("x", "same")], // x already "same"
        };

        var result = _runtime.Run([rule], new AutomationEvent(AutomationTrigger.FieldChanged, "x"), Values(("x", "same")));

        // The rule fired once on the initial event; the no-op set did not re-trigger it.
        Assert.Equal(["noop-set"], result.FiredRules);
    }

    // ----- AI action seam (stubbed, no network) -----

    [Fact]
    public void Ai_action_seam_is_invoked_and_no_op_provider_does_nothing()
    {
        var rule = new AutomationRule
        {
            Name = "corrosion-ai",
            Trigger = AutomationTrigger.RecordSave,
            Actions = [new RunAiAction("classify-corrosion", "Does the photo show corrosion?")],
        };

        // Default runtime uses the shipped NoOpAiActionProvider.
        var result = _runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordSave), Values());

        var invocation = Assert.Single(result.AiInvocations);
        Assert.Equal("classify-corrosion", invocation.ActionId);
        Assert.False(invocation.Handled); // seam reached, but stub handled nothing
        Assert.Empty(result.Alerts);
        Assert.False(result.Values.ContainsKey("condition"));
    }

    [Fact]
    public void A_wired_ai_provider_folds_its_field_writes_back_into_the_record()
    {
        var runtime = new AutomationRuntime(new FakeCorrosionProvider());
        var rule = new AutomationRule
        {
            Name = "corrosion-ai",
            Trigger = AutomationTrigger.RecordSave,
            Actions = [new RunAiAction("classify-corrosion")],
        };

        var result = runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordSave), Values());

        Assert.True(Assert.Single(result.AiInvocations).Handled);
        Assert.Equal("Poor", result.Values["condition"]);
        Assert.Equal(true, result.Values["needsReview"]);
        Assert.Equal("Detected corrosion.", Assert.Single(result.Alerts).Message);
    }

    [Fact]
    public void Default_no_op_provider_claims_nothing()
    {
        var provider = new NoOpAiActionProvider();
        Assert.False(provider.CanHandle("anything"));
        Assert.Empty(provider.Run(new AiActionRequest("anything", null, Values())).FieldWrites);
    }

    /// <summary>A deterministic, offline test double standing in for a live AI provider — no network.</summary>
    private sealed class FakeCorrosionProvider : IAiActionProvider
    {
        public bool CanHandle(string actionId) => actionId == "classify-corrosion";

        public AiActionResult Run(AiActionRequest request) => new(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["condition"] = "Poor",
                ["needsReview"] = true,
            },
            "Detected corrosion.");
    }
}
