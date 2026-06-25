using Honua.Collect.Core.Automation;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>
/// Tests for the #44 Core test harness: running a rule-set against a sample record
/// and asserting the resulting field changes and action effects deterministically —
/// the engine the console authoring/test UI drives, verifiable here without a device
/// or network. Covers single-event effects and the full record lifecycle (the
/// definition-of-done scenario: a corrosion photo on save sets condition=Poor, flags
/// for review, and queues a webhook).
/// </summary>
public class AutomationTestRunnerTests
{
    private readonly AutomationTestRunner _runner = new();

    private static Dictionary<string, object?> Sample(params (string Id, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void Reports_field_changes_with_before_and_after()
    {
        var rules = new[]
        {
            new AutomationRule
            {
                Name = "area",
                Trigger = AutomationTrigger.RecordSave,
                Actions = [new ComputeFieldAction("area", "${length} * ${width}")],
            },
        };

        var result = _runner.Run(rules, AutomationTrigger.RecordSave,
            Sample(("length", 4), ("width", 3)));

        Assert.Contains("area", result.Effect.ChangedFields);
        Assert.Equal(12d, Convert.ToDouble(result.Effect.ValueOf("area")));
        // Inputs unchanged → not reported as changed.
        Assert.DoesNotContain("length", result.Effect.ChangedFields);
        Assert.Equal("area", Assert.Single(result.FiredRules));
    }

    [Fact]
    public void Reports_no_changes_when_no_rule_fires()
    {
        var rules = new[]
        {
            new AutomationRule
            {
                Name = "only-on-save",
                Trigger = AutomationTrigger.RecordSave,
                Actions = [new SetFieldAction("flag", true)],
            },
        };

        // Fire a different trigger — the rule should not match.
        var result = _runner.Run(rules, AutomationTrigger.RecordNew, Sample(("flag", false)));

        Assert.False(result.Effect.HasFieldChanges);
        Assert.Empty(result.FiredRules);
    }

    [Fact]
    public void Surfaces_validation_errors_as_invalid()
    {
        var rules = new[]
        {
            new AutomationRule
            {
                Name = "require-photo",
                Trigger = AutomationTrigger.Validate,
                Condition = new RuleCondition("photo", ConditionOperator.NotExists),
                Actions = [new InvalidateAction("A photo is required.")],
            },
        };

        var result = _runner.Run(rules, AutomationTrigger.Validate, Sample(("photo", null)));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Result.ValidationErrors);
        Assert.Equal("A photo is required.", error.Message);
    }

    [Fact]
    public void Lifecycle_run_settles_corrosion_scenario_on_save()
    {
        // The #44 definition-of-done: on saving a record whose corrosion photo flag is
        // set, condition=Poor, the record is flagged for review, and a webhook queues.
        var rules = new[]
        {
            new AutomationRule
            {
                Name = "corrosion-condition",
                Trigger = AutomationTrigger.FieldChanged,
                TriggerFieldId = "corrosion",
                Condition = new ExpressionCondition("${corrosion} = true"),
                Actions =
                [
                    new SetFieldAction("condition", "Poor"),
                    new AddTagAction("review"),
                ],
            },
            new AutomationRule
            {
                Name = "poor-webhook",
                Trigger = AutomationTrigger.RecordSave,
                Condition = new ExpressionCondition("${condition} = 'Poor'"),
                Actions =
                [
                    HttpRequestAction.Post(
                        "https://hooks.test/flag", "{\"flag\":\"review\"}", "rec-7-review"),
                ],
            },
        };

        var steps = _runner.RunLifecycle(
            rules,
            Sample(("condition", "Good")),
            edits: [new KeyValuePair<string, object?>("corrosion", true)]);

        // Steps: RecordNew, FieldChanged(corrosion), Validate, RecordSave.
        Assert.Equal(4, steps.Count);

        var fieldChange = steps[1];
        Assert.Equal("Poor", fieldChange.Effect.ValueOf("condition"));
        Assert.Contains("review", fieldChange.Result.Tags);

        var save = steps[^1];
        var queued = Assert.Single(save.Result.HttpRequests);
        Assert.Equal("rec-7-review", queued.Request.IdempotencyKey);
        Assert.Equal("poor-webhook", queued.RuleName);

        // The settled record carries condition=Poor through to save.
        Assert.Equal("Poor", save.Effect.ValueOf("condition"));
    }

    [Fact]
    public void Removed_field_is_reported_as_a_change()
    {
        // A rule that clears a field by computing from a missing input leaves it null;
        // the before/after diff reports the key as changed (value modified to null).
        var rules = new[]
        {
            new AutomationRule
            {
                Name = "clear-temp",
                Trigger = AutomationTrigger.RecordSave,
                Actions = [new SetFieldAction("scratch", null)],
            },
        };

        var result = _runner.Run(rules, AutomationTrigger.RecordSave, Sample(("scratch", "x")));

        Assert.Contains("scratch", result.Effect.ChangedFields);
        Assert.Null(result.Effect.ValueOf("scratch"));
    }
}
