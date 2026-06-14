namespace Honua.Collect.Core.Automation;

/// <summary>
/// A safe, offline automation runtime (BACKLOG, #44) — the engine behind
/// programmable Data Events. Given a set of rules, an event, and the record's
/// current values, it fires every matching rule (right trigger, field, and
/// condition) and applies their actions deterministically: set-field updates fold
/// into the values, alerts/validation errors/queued requests accumulate. No
/// arbitrary code runs and nothing touches the network, so it works with no
/// signal; queued HTTP requests are replayed later by the sync layer.
/// </summary>
public sealed class AutomationRuntime
{
    /// <summary>
    /// Runs the rules for an event. Set-field actions feed forward, so a later rule's
    /// condition sees an earlier rule's writes (rules fire in list order).
    /// </summary>
    /// <param name="rules">The authored rules.</param>
    /// <param name="event">The triggering event.</param>
    /// <param name="values">The record's current values.</param>
    /// <returns>The result: updated values, alerts, validation errors, queued requests, and fired rule names.</returns>
    public AutomationResult Run(
        IEnumerable<AutomationRule> rules,
        AutomationEvent @event,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(values);

        var working = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        var alerts = new List<AutomationAlert>();
        var errors = new List<AutomationValidationError>();
        var queued = new List<QueuedRequest>();
        var fired = new List<string>();

        foreach (var rule in rules)
        {
            if (!Matches(rule, @event, working))
            {
                continue;
            }

            fired.Add(rule.Name);
            foreach (var action in rule.Actions)
            {
                Apply(action, rule.Name, working, alerts, errors, queued);
            }
        }

        return new AutomationResult
        {
            Values = working,
            Alerts = alerts,
            ValidationErrors = errors,
            QueuedRequests = queued,
            FiredRules = fired,
        };
    }

    private static bool Matches(AutomationRule rule, AutomationEvent @event, IReadOnlyDictionary<string, object?> values)
    {
        if (rule.Trigger != @event.Trigger)
        {
            return false;
        }

        // A field-scoped rule only fires for its field's change.
        if (rule.Trigger == AutomationTrigger.FieldChanged
            && rule.TriggerFieldId is not null
            && !string.Equals(rule.TriggerFieldId, @event.ChangedFieldId, StringComparison.Ordinal))
        {
            return false;
        }

        return rule.Condition is null || rule.Condition.Matches(values);
    }

    private static void Apply(
        AutomationAction action,
        string ruleName,
        Dictionary<string, object?> values,
        List<AutomationAlert> alerts,
        List<AutomationValidationError> errors,
        List<QueuedRequest> queued)
    {
        switch (action)
        {
            case SetFieldAction set:
                values[set.FieldId] = set.Value;
                break;
            case AlertAction alert:
                alerts.Add(new AutomationAlert(alert.Severity, alert.Message, ruleName));
                break;
            case InvalidateAction invalidate:
                errors.Add(new AutomationValidationError(invalidate.Message, ruleName));
                break;
            case QueueRequestAction request:
                queued.Add(new QueuedRequest(request.Url, request.Body, ruleName));
                break;
        }
    }
}
