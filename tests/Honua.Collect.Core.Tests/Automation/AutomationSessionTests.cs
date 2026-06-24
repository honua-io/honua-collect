using Honua.Collect.Core.Automation;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>
/// Tests that the automation engine is wired into the live <see cref="FormSession"/>
/// record lifecycle (#44): field-change events fire on value edits, save/validate
/// hooks fire their triggers, and set-field results fold back into the session.
/// </summary>
public class AutomationSessionTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "inspection",
        Name = "Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields =
                [
                    new FormField { FieldId = "hasCorrosion", Label = "Corrosion?", Type = FormFieldType.YesNo },
                    new FormField { FieldId = "condition", Label = "Condition", Type = FormFieldType.Text },
                    new FormField { FieldId = "needsReview", Label = "Review?", Type = FormFieldType.YesNo },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Text },
                ],
            },
        ],
    };

    [Fact]
    public void Field_change_in_the_session_fires_a_rule_and_writes_back()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var rule = new AutomationRule
        {
            Name = "corrosion",
            Trigger = AutomationTrigger.FieldChanged,
            TriggerFieldId = "hasCorrosion",
            Condition = new RuleCondition("hasCorrosion", ConditionOperator.Equals, true),
            Actions = [new SetFieldAction("condition", "Poor"), new SetFieldAction("needsReview", true)],
        };

        using var automation = AutomationSession.Attach(session, [rule]);

        session.SetValue("hasCorrosion", true);

        // The rule's set-field writes were folded back into the live session.
        Assert.Equal("Poor", session.GetValue("condition"));
        Assert.Equal(true, session.GetValue("needsReview"));
        Assert.Equal(["corrosion"], automation.LastResult!.FiredRules);
    }

    [Fact]
    public void Save_hook_fires_record_save_rules_and_queues_a_webhook_offline()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        session.SetValue("hasCorrosion", true);

        var rule = new AutomationRule
        {
            Name = "queue-webhook",
            Trigger = AutomationTrigger.RecordSave,
            Condition = new RuleCondition("hasCorrosion", ConditionOperator.Equals, true),
            Actions = [new QueueRequestAction("https://ops.example/webhook", "{\"flag\":\"corrosion\"}")],
        };

        using var automation = AutomationSession.Attach(session, [rule]);

        var result = automation.RaiseRecordSave();

        var queued = Assert.Single(result.QueuedRequests);
        Assert.Equal("https://ops.example/webhook", queued.Url);
    }

    [Fact]
    public void Validate_hook_surfaces_a_blocking_automation_validation_error()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var rule = new AutomationRule
        {
            Name = "require-photo",
            Trigger = AutomationTrigger.Validate,
            Condition = new RuleCondition("photo", ConditionOperator.NotExists),
            Actions = [new InvalidateAction("A photo is required.")],
        };

        using var automation = AutomationSession.Attach(session, [rule]);

        var result = automation.RaiseValidate();

        Assert.False(result.IsValid);
        Assert.Equal("A photo is required.", Assert.Single(result.ValidationErrors).Message);
    }

    [Fact]
    public void Write_back_does_not_recurse_infinitely()
    {
        // A rule whose own write would re-raise FieldChanged must not spin: the
        // session's re-entrancy guard plus the runtime's loop guard contain it.
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var rule = new AutomationRule
        {
            Name = "echo",
            Trigger = AutomationTrigger.FieldChanged,
            TriggerFieldId = "condition",
            Actions = [new SetFieldAction("needsReview", true)],
        };

        using var automation = AutomationSession.Attach(session, [rule]);

        session.SetValue("condition", "Poor"); // must return, not hang

        Assert.Equal(true, session.GetValue("needsReview"));
    }
}
