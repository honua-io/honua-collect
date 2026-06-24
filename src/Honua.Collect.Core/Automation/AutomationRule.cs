namespace Honua.Collect.Core.Automation;

/// <summary>
/// A single offline automation rule: when <see cref="Trigger"/> fires (optionally
/// only for <see cref="TriggerFieldId"/>) and <see cref="Condition"/> holds, run
/// <see cref="Actions"/> in order. Authored alongside the form definition.
/// </summary>
public sealed record AutomationRule
{
    /// <summary>A human name for the rule (shown in authoring and surfaced in results).</summary>
    public required string Name { get; init; }

    /// <summary>The lifecycle point that fires the rule.</summary>
    public required AutomationTrigger Trigger { get; init; }

    /// <summary>
    /// For a <see cref="AutomationTrigger.FieldChanged"/> trigger, restricts the rule to
    /// changes of this field; null fires for any field change.
    /// </summary>
    public string? TriggerFieldId { get; init; }

    /// <summary>
    /// Optional guard; when null the rule always fires for its trigger. May be the
    /// data-driven <see cref="RuleCondition"/> or an <see cref="ExpressionCondition"/>
    /// reusing the SDK form-expression engine.
    /// </summary>
    public IRuleCondition? Condition { get; init; }

    /// <summary>The actions to run, in order, when the rule fires.</summary>
    public IReadOnlyList<AutomationAction> Actions { get; init; } = [];
}

/// <summary>The event handed to the runtime: what happened, and (for a field change) to which field.</summary>
/// <param name="Trigger">The lifecycle point that occurred.</param>
/// <param name="ChangedFieldId">The field that changed, for a <see cref="AutomationTrigger.FieldChanged"/> event.</param>
public sealed record AutomationEvent(AutomationTrigger Trigger, string? ChangedFieldId = null);
