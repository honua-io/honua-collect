using Honua.Sdk.Field.Forms.Expressions;

namespace Honua.Collect.Core.Automation;

/// <summary>
/// A safe, offline automation runtime (BACKLOG, #44) — the engine behind
/// programmable Data Events. Given a set of rules, an event, and the record's
/// current values, it fires every matching rule (right trigger, field, and
/// condition) and applies their actions deterministically: set-field / computed
/// updates fold into the values; alerts, validation errors, tags, queued requests,
/// notifications, follow-ups, and AI-action invocations accumulate. No arbitrary
/// code runs and nothing touches the network, so it works with no signal; queued
/// HTTP requests and notifications are replayed later by the host/sync layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cascading + loop guard.</b> When an action changes a field's value, any
/// <see cref="AutomationTrigger.FieldChanged"/> rules keyed to that field are
/// re-evaluated, in deterministic order, so derived fields settle. A field-set
/// that re-triggers itself (A sets B, B sets A) would loop forever, so the cascade
/// is bounded two ways: each field is re-triggered at most once per run, and the
/// total cascade depth is capped (<see cref="MaxCascadeDepth"/>). Conditions and
/// computed values reuse the SDK form-expression engine — the same language as
/// field visibility — so there is no second expression dialect.
/// </para>
/// </remarks>
public sealed class AutomationRuntime
{
    /// <summary>
    /// Maximum number of cascade waves after the initial event before the runtime
    /// stops re-triggering, as a backstop against pathological rule graphs.
    /// </summary>
    public const int MaxCascadeDepth = 64;

    private readonly IAiActionProvider _aiProvider;

    /// <summary>Creates a runtime with the no-op AI provider (the shipped default).</summary>
    public AutomationRuntime()
        : this(new NoOpAiActionProvider())
    {
    }

    /// <summary>Creates a runtime backed by a specific AI-action provider.</summary>
    /// <param name="aiProvider">Resolves and runs <see cref="RunAiAction"/> steps.</param>
    public AutomationRuntime(IAiActionProvider aiProvider)
        => _aiProvider = aiProvider ?? throw new ArgumentNullException(nameof(aiProvider));

    /// <summary>
    /// Runs the rules for an event. Rules fire in list order, so a later rule's
    /// condition sees an earlier rule's writes; field changes then cascade into
    /// dependent <see cref="AutomationTrigger.FieldChanged"/> rules, loop-guarded.
    /// </summary>
    /// <param name="rules">The authored rules.</param>
    /// <param name="event">The triggering event.</param>
    /// <param name="values">The record's current values.</param>
    /// <returns>The result: updated values, alerts, validation errors, tags, queued requests/notifications, follow-ups, AI invocations, and fired rule names.</returns>
    public AutomationResult Run(
        IEnumerable<AutomationRule> rules,
        AutomationEvent @event,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(values);

        // Snapshot the rules so order is stable across cascade waves and the
        // enumerable is walked once.
        var ruleList = rules as IReadOnlyList<AutomationRule> ?? rules.ToList();

        var acc = new Accumulator(values);

        // Process the initial event, then drain any field-change cascades it caused.
        var queue = new Queue<AutomationEvent>();
        queue.Enqueue(@event);

        // A field is re-triggered at most once per run (the loop guard): once its
        // FieldChanged rules have run, further writes to it do not re-enqueue it,
        // so A↔B set-loops terminate. Seed with the originating field so the field
        // that started the run is not itself re-cascaded.
        var cascadedFields = new HashSet<string>(StringComparer.Ordinal);
        if (@event is { Trigger: AutomationTrigger.FieldChanged, ChangedFieldId: { } seedField })
        {
            cascadedFields.Add(seedField);
        }

        var depth = 0;

        while (queue.Count > 0 && depth <= MaxCascadeDepth)
        {
            var current = queue.Dequeue();

            foreach (var rule in ruleList)
            {
                if (!Matches(rule, current, acc.Values))
                {
                    continue;
                }

                acc.Fired.Add(rule.Name);
                foreach (var action in rule.Actions)
                {
                    Apply(action, rule.Name, acc);
                }
            }

            // Enqueue a FieldChanged cascade for each field whose value actually
            // changed during this wave and that has not already cascaded.
            foreach (var changed in acc.DrainChangedFields())
            {
                if (cascadedFields.Add(changed))
                {
                    queue.Enqueue(new AutomationEvent(AutomationTrigger.FieldChanged, changed));
                }
            }

            depth++;
        }

        return acc.ToResult();
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

    private void Apply(AutomationAction action, string ruleName, Accumulator acc)
    {
        switch (action)
        {
            case SetFieldAction set:
                acc.SetField(set.FieldId, set.Value);
                break;
            case ComputeFieldAction compute:
                acc.SetField(compute.FieldId, ExpressionEvaluator.Evaluate(compute.Expression, acc.Values));
                break;
            case AlertAction alert:
                acc.Alerts.Add(new AutomationAlert(alert.Severity, alert.Message, ruleName));
                break;
            case InvalidateAction invalidate:
                acc.Errors.Add(new AutomationValidationError(invalidate.Message, ruleName));
                break;
            case AddTagAction tag:
                acc.AddTag(tag.Tag);
                break;
            case QueueRequestAction request:
                acc.Queued.Add(new QueuedRequest(request.Url, request.Body, ruleName));
                break;
            case HttpRequestAction http:
                acc.HttpRequests.Add(new QueuedHttpRequest(http.Request, ruleName));
                break;
            case OpenUrlAction openUrl:
                acc.OpenUrlIntents.Add(new OpenUrlIntent(openUrl.Url, openUrl.Target, ruleName));
                break;
            case EnqueueNotificationAction note:
                acc.Notifications.Add(new QueuedNotification(note.Title, note.Body, ruleName));
                break;
            case ScheduleFollowUpAction followUp:
                acc.FollowUps.Add(new ScheduledFollowUp(followUp.Description, followUp.DueInDays, ruleName));
                break;
            case RunAiAction ai:
                RunAi(ai, ruleName, acc);
                break;
        }
    }

    private void RunAi(RunAiAction ai, string ruleName, Accumulator acc)
    {
        var handled = _aiProvider.CanHandle(ai.ActionId);
        acc.AiInvocations.Add(new AiActionInvocation(ai.ActionId, handled, ruleName));

        if (!handled)
        {
            return; // No-op stub path: seam exercised, nothing runs.
        }

        var result = _aiProvider.Run(new AiActionRequest(ai.ActionId, ai.Prompt, acc.Values));
        foreach (var (field, value) in result.FieldWrites)
        {
            acc.SetField(field, value);
        }

        if (!string.IsNullOrWhiteSpace(result.Note))
        {
            acc.Alerts.Add(new AutomationAlert(AutomationSeverity.Info, result.Note, ruleName));
        }
    }

    /// <summary>
    /// Mutable run state. Tracks the working values, every accumulated output, and
    /// the set of fields whose value changed since the last cascade wave (so only
    /// real changes re-trigger).
    /// </summary>
    private sealed class Accumulator
    {
        private readonly Dictionary<string, object?> _working;
        private readonly HashSet<string> _changedThisWave = new(StringComparer.Ordinal);
        private readonly List<string> _tags = [];
        private readonly HashSet<string> _tagSet = new(StringComparer.Ordinal);

        public Accumulator(IReadOnlyDictionary<string, object?> values)
            => _working = new Dictionary<string, object?>(values, StringComparer.Ordinal);

        public IReadOnlyDictionary<string, object?> Values => _working;

        public List<AutomationAlert> Alerts { get; } = [];

        public List<AutomationValidationError> Errors { get; } = [];

        public List<QueuedRequest> Queued { get; } = [];

        public List<QueuedHttpRequest> HttpRequests { get; } = [];

        public List<OpenUrlIntent> OpenUrlIntents { get; } = [];

        public List<QueuedNotification> Notifications { get; } = [];

        public List<ScheduledFollowUp> FollowUps { get; } = [];

        public List<AiActionInvocation> AiInvocations { get; } = [];

        public List<string> Fired { get; } = [];

        public void SetField(string fieldId, object? value)
        {
            var existed = _working.TryGetValue(fieldId, out var current);
            if (existed && AutomationValue.AreEqual(current, value))
            {
                return; // No real change — do not mark for cascade.
            }

            _working[fieldId] = value;
            _changedThisWave.Add(fieldId);
        }

        public void AddTag(string tag)
        {
            if (_tagSet.Add(tag))
            {
                _tags.Add(tag);
            }
        }

        public IEnumerable<string> DrainChangedFields()
        {
            // Deterministic order: sort the wave's changed fields.
            var changed = _changedThisWave.OrderBy(f => f, StringComparer.Ordinal).ToList();
            _changedThisWave.Clear();
            return changed;
        }

        public AutomationResult ToResult() => new()
        {
            Values = _working,
            Alerts = Alerts,
            ValidationErrors = Errors,
            QueuedRequests = Queued,
            HttpRequests = HttpRequests,
            OpenUrlIntents = OpenUrlIntents,
            Notifications = Notifications,
            FollowUps = FollowUps,
            Tags = _tags,
            AiInvocations = AiInvocations,
            FiredRules = Fired,
        };
    }
}
