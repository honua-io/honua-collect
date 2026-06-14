using Honua.Collect.Core.Automation;

namespace Honua.Collect.Core.Tests.Automation;

public class AutomationRuntimeTests
{
    private static Dictionary<string, object?> Values(params (string Id, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal);

    private readonly AutomationRuntime _runtime = new();

    // The motivating example from #44: on save, if the corrosion photo flag is set,
    // set condition=Poor, flag for review, and queue a webhook — all offline.
    [Fact]
    public void Corrosion_rule_sets_fields_flags_review_and_queues_a_webhook()
    {
        var rule = new AutomationRule
        {
            Name = "corrosion",
            Trigger = AutomationTrigger.RecordSave,
            Condition = new RuleCondition("hasCorrosion", ConditionOperator.Equals, true),
            Actions =
            [
                new SetFieldAction("condition", "Poor"),
                new SetFieldAction("needsReview", true),
                new AlertAction(AutomationSeverity.Warning, "Flagged for review: corrosion detected."),
                new QueueRequestAction("https://ops.example/webhook", "{\"flag\":\"corrosion\"}"),
            ],
        };

        var result = _runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordSave), Values(("hasCorrosion", true)));

        Assert.Equal("Poor", result.Values["condition"]);
        Assert.Equal(true, result.Values["needsReview"]);
        Assert.Single(result.Alerts);
        Assert.Equal(AutomationSeverity.Warning, result.Alerts[0].Severity);
        var queued = Assert.Single(result.QueuedRequests);
        Assert.Equal("https://ops.example/webhook", queued.Url);
        Assert.Equal(["corrosion"], result.FiredRules);
    }

    [Fact]
    public void A_rule_whose_condition_is_false_does_not_fire()
    {
        var rule = new AutomationRule
        {
            Name = "r",
            Trigger = AutomationTrigger.RecordSave,
            Condition = new RuleCondition("hasCorrosion", ConditionOperator.Equals, true),
            Actions = [new SetFieldAction("condition", "Poor")],
        };

        var result = _runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordSave), Values(("hasCorrosion", false)));

        Assert.Empty(result.FiredRules);
        Assert.False(result.Values.ContainsKey("condition"));
    }

    [Fact]
    public void Trigger_and_field_scoping_are_respected()
    {
        var rule = new AutomationRule
        {
            Name = "on-status-change",
            Trigger = AutomationTrigger.FieldChanged,
            TriggerFieldId = "status",
            Actions = [new SetFieldAction("touched", true)],
        };

        // Wrong trigger.
        Assert.Empty(_runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordSave), Values()).FiredRules);
        // Right trigger, wrong field.
        Assert.Empty(_runtime.Run([rule], new AutomationEvent(AutomationTrigger.FieldChanged, "notes"), Values()).FiredRules);
        // Right trigger and field.
        Assert.Single(_runtime.Run([rule], new AutomationEvent(AutomationTrigger.FieldChanged, "status"), Values()).FiredRules);
    }

    [Fact]
    public void Invalidate_action_blocks_save()
    {
        var rule = new AutomationRule
        {
            Name = "require-photo",
            Trigger = AutomationTrigger.Validate,
            Condition = new RuleCondition("photo", ConditionOperator.NotExists),
            Actions = [new InvalidateAction("A photo is required.")],
        };

        var result = _runtime.Run([rule], new AutomationEvent(AutomationTrigger.Validate), Values(("photo", null)));

        Assert.False(result.IsValid);
        Assert.Equal("A photo is required.", Assert.Single(result.ValidationErrors).Message);
    }

    [Fact]
    public void Set_field_writes_feed_forward_to_later_rules()
    {
        var rules = new[]
        {
            new AutomationRule
            {
                Name = "compute-grade",
                Trigger = AutomationTrigger.RecordSave,
                Condition = new RuleCondition("score", ConditionOperator.LessThan, 50),
                Actions = [new SetFieldAction("grade", "F")],
            },
            new AutomationRule
            {
                Name = "flag-failing",
                Trigger = AutomationTrigger.RecordSave,
                Condition = new RuleCondition("grade", ConditionOperator.Equals, "F"), // sees the prior rule's write
                Actions = [new SetFieldAction("needsReview", true)],
            },
        };

        var result = _runtime.Run(rules, new AutomationEvent(AutomationTrigger.RecordSave), Values(("score", 30)));

        Assert.Equal("F", result.Values["grade"]);
        Assert.Equal(true, result.Values["needsReview"]);
        Assert.Equal(["compute-grade", "flag-failing"], result.FiredRules);
    }

    [Theory]
    [InlineData(ConditionOperator.GreaterThan, 10.0, 5.0, true)]
    [InlineData(ConditionOperator.GreaterThan, 5.0, 10.0, false)]
    [InlineData(ConditionOperator.LessThan, 5.0, 10.0, true)]
    [InlineData(ConditionOperator.Equals, 7.0, 7.0, true)]
    [InlineData(ConditionOperator.NotEquals, 7.0, 8.0, true)]
    public void Numeric_conditions_evaluate(ConditionOperator op, double actual, double comparand, bool expected)
    {
        var condition = new RuleCondition("reading", op, comparand);
        Assert.Equal(expected, condition.Matches(Values(("reading", actual))));
    }

    [Fact]
    public void Exists_and_numeric_string_coercion_work()
    {
        Assert.True(new RuleCondition("f", ConditionOperator.Exists).Matches(Values(("f", "x"))));
        Assert.True(new RuleCondition("f", ConditionOperator.NotExists).Matches(Values(("f", ""))));
        // String "12" compares numerically against 5.
        Assert.True(new RuleCondition("f", ConditionOperator.GreaterThan, 5).Matches(Values(("f", "12"))));
        // Non-numeric comparand falls back to ordinal string equality.
        Assert.True(new RuleCondition("f", ConditionOperator.Equals, "open").Matches(Values(("f", "open"))));
    }

    [Fact]
    public void Run_does_not_mutate_the_input_values()
    {
        var input = Values(("a", "1"));
        var rule = new AutomationRule
        {
            Name = "r",
            Trigger = AutomationTrigger.RecordNew,
            Actions = [new SetFieldAction("a", "2")],
        };

        _runtime.Run([rule], new AutomationEvent(AutomationTrigger.RecordNew), input);

        Assert.Equal("1", input["a"]); // untouched
    }

    [Fact]
    public void Run_guards_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => _runtime.Run(null!, new AutomationEvent(AutomationTrigger.RecordNew), Values()));
        Assert.Throws<ArgumentNullException>(() => _runtime.Run([], null!, Values()));
        Assert.Throws<ArgumentNullException>(() => _runtime.Run([], new AutomationEvent(AutomationTrigger.RecordNew), null!));
        Assert.Throws<ArgumentNullException>(() => new RuleCondition("f", ConditionOperator.Equals, 1).Matches(null!));
    }
}
