using Honua.Collect.Core.Automation;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>
/// Tests for the #44 platform-neutral open-URL action model: the runtime records an
/// <see cref="OpenUrlIntent"/> the host launches — no network, no platform launch in
/// Core, with the target preserved.
/// </summary>
public class OpenUrlActionTests
{
    private readonly AutomationRuntime _runtime = new();

    [Fact]
    public void Open_url_action_records_a_platform_neutral_intent()
    {
        var rule = new AutomationRule
        {
            Name = "open-spec",
            Trigger = AutomationTrigger.RecordSave,
            Actions = [new OpenUrlAction("https://docs.test/spec", OpenUrlTarget.InApp)],
        };

        var result = _runtime.Run(
            [rule],
            new AutomationEvent(AutomationTrigger.RecordSave),
            new Dictionary<string, object?>());

        var intent = Assert.Single(result.OpenUrlIntents);
        Assert.Equal("https://docs.test/spec", intent.Url);
        Assert.Equal(OpenUrlTarget.InApp, intent.Target);
        Assert.Equal("open-spec", intent.RuleName);
    }

    [Fact]
    public void Open_url_defaults_target_and_supports_non_http_schemes()
    {
        var rule = new AutomationRule
        {
            Name = "call-office",
            Trigger = AutomationTrigger.RecordNew,
            Actions = [new OpenUrlAction("tel:+18005551234")],
        };

        var result = _runtime.Run(
            [rule],
            new AutomationEvent(AutomationTrigger.RecordNew),
            new Dictionary<string, object?>());

        var intent = Assert.Single(result.OpenUrlIntents);
        Assert.Equal("tel:+18005551234", intent.Url);
        Assert.Equal(OpenUrlTarget.Default, intent.Target);
    }

    [Fact]
    public void Open_url_is_gated_by_its_rule_condition()
    {
        var rule = new AutomationRule
        {
            Name = "open-when-failed",
            Trigger = AutomationTrigger.Validate,
            Condition = new ExpressionCondition("${status} = 'failed'"),
            Actions = [new OpenUrlAction("https://docs.test/remediate")],
        };

        var passing = _runtime.Run(
            [rule],
            new AutomationEvent(AutomationTrigger.Validate),
            new Dictionary<string, object?> { ["status"] = "ok" });
        Assert.Empty(passing.OpenUrlIntents);

        var failing = _runtime.Run(
            [rule],
            new AutomationEvent(AutomationTrigger.Validate),
            new Dictionary<string, object?> { ["status"] = "failed" });
        Assert.Single(failing.OpenUrlIntents);
    }
}
