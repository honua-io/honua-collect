namespace Honua.Collect.Core.Automation;

/// <summary>
/// The field changes a rule-set produced for one sample record: the values before
/// and after, and just the keys whose value actually changed (plus whether each was
/// added, removed, or modified). This is the assertable core of an automation test.
/// </summary>
/// <param name="Before">The sample record's values as given.</param>
/// <param name="After">The values after the rules ran.</param>
public sealed record AutomationEffect(
    IReadOnlyDictionary<string, object?> Before,
    IReadOnlyDictionary<string, object?> After)
{
    /// <summary>The field keys whose value changed (added, removed, or modified).</summary>
    public IReadOnlyList<string> ChangedFields { get; } = ComputeChanged(Before, After);

    /// <summary>Whether the run changed any field value.</summary>
    public bool HasFieldChanges => ChangedFields.Count > 0;

    /// <summary>The after-value of a field (convenience for assertions).</summary>
    /// <param name="fieldId">The field to read.</param>
    /// <returns>The value, or null if absent.</returns>
    public object? ValueOf(string fieldId)
        => After.TryGetValue(fieldId, out var value) ? value : null;

    private static IReadOnlyList<string> ComputeChanged(
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after)
    {
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in after)
        {
            if (!before.TryGetValue(key, out var old) || !AutomationValue.AreEqual(old, value))
            {
                changed.Add(key);
            }
        }

        foreach (var key in before.Keys)
        {
            if (!after.ContainsKey(key))
            {
                changed.Add(key);
            }
        }

        return changed.ToList();
    }
}

/// <summary>
/// The full effect set of running a rule-set against a sample record for one event:
/// the field changes plus every accumulated output (alerts, validation errors, tags,
/// queued HTTP / lightweight requests, open-URL intents, notifications, follow-ups,
/// AI invocations) and which rules fired. Everything an author needs to assert.
/// </summary>
/// <param name="Event">The trigger the run was for.</param>
/// <param name="Result">The raw runtime result.</param>
public sealed record AutomationTestResult(AutomationEvent Event, AutomationResult Result)
{
    /// <summary>The before/after field effect.</summary>
    public AutomationEffect Effect { get; init; } = default!;

    /// <summary>Names of the rules that fired, in order.</summary>
    public IReadOnlyList<string> FiredRules => Result.FiredRules;

    /// <summary>Whether the sample record passed automation validation.</summary>
    public bool IsValid => Result.IsValid;
}

/// <summary>
/// A deterministic, offline test harness for automations (BACKLOG #44 — "test
/// harness"). Given an authored rule-set and a sample record, it runs the rules
/// through the <see cref="AutomationRuntime"/> for a chosen trigger (or the full
/// new→edits→validate→save lifecycle) and returns the resulting effect set —
/// field changes, alerts, validation errors, tags, queued HTTP requests, open-URL
/// intents, notifications, follow-ups, AI invocations, and fired rules — so an
/// author (or a console test runner) can assert "on save, condition=Poor and a
/// webhook is queued" without a device, a network, or live connectivity.
/// </summary>
/// <remarks>
/// The authoring/versioning UI itself lives in honua-console; this is the
/// Core-verifiable engine those tools (and xUnit) drive. It is a thin, explicit
/// wrapper over the runtime — no hidden state — so the same rules behave identically
/// here and in a live <see cref="AutomationSession"/>.
/// </remarks>
public sealed class AutomationTestRunner
{
    private readonly AutomationRuntime _runtime;

    /// <summary>Creates a runner over the no-op AI provider (the shipped default).</summary>
    public AutomationTestRunner()
        : this(new AutomationRuntime())
    {
    }

    /// <summary>Creates a runner backed by a specific runtime (e.g. with a fake AI provider).</summary>
    /// <param name="runtime">The runtime to drive.</param>
    public AutomationTestRunner(AutomationRuntime runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>
    /// Runs the rule-set against a sample record for a single event and returns the
    /// effect set.
    /// </summary>
    /// <param name="rules">The authored rules.</param>
    /// <param name="event">The triggering event.</param>
    /// <param name="sample">The sample record's field values.</param>
    /// <returns>The effect set, including before/after field changes.</returns>
    public AutomationTestResult Run(
        IEnumerable<AutomationRule> rules,
        AutomationEvent @event,
        IReadOnlyDictionary<string, object?> sample)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(sample);

        var before = Snapshot(sample);
        var result = _runtime.Run(rules, @event, sample);
        return new AutomationTestResult(@event, result)
        {
            Effect = new AutomationEffect(before, result.Values),
        };
    }

    /// <summary>
    /// Convenience overload that runs a trigger with no field scope (i.e. not a
    /// field-change event).
    /// </summary>
    /// <param name="rules">The authored rules.</param>
    /// <param name="trigger">The trigger to fire.</param>
    /// <param name="sample">The sample record's field values.</param>
    /// <returns>The effect set.</returns>
    public AutomationTestResult Run(
        IEnumerable<AutomationRule> rules,
        AutomationTrigger trigger,
        IReadOnlyDictionary<string, object?> sample)
        => Run(rules, new AutomationEvent(trigger), sample);

    /// <summary>
    /// Runs the full record lifecycle over a sample — <see cref="AutomationTrigger.RecordNew"/>,
    /// then a <see cref="AutomationTrigger.FieldChanged"/> for each supplied edit
    /// (applied in order against the carried-forward values), then
    /// <see cref="AutomationTrigger.Validate"/> and <see cref="AutomationTrigger.RecordSave"/> —
    /// returning the per-step effects so an author can assert end-to-end behaviour
    /// (the #44 definition-of-done scenario: a corrosion photo on save sets
    /// condition=Poor, flags for review, and queues a webhook). The final step's
    /// <see cref="AutomationEffect.After"/> is the record's settled state.
    /// </summary>
    /// <param name="rules">The authored rules.</param>
    /// <param name="sample">The starting sample record.</param>
    /// <param name="edits">Field edits to apply (each fires a FieldChanged), in order.</param>
    /// <returns>The ordered per-step results.</returns>
    public IReadOnlyList<AutomationTestResult> RunLifecycle(
        IEnumerable<AutomationRule> rules,
        IReadOnlyDictionary<string, object?> sample,
        IEnumerable<KeyValuePair<string, object?>>? edits = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(sample);

        var ruleList = rules as IReadOnlyList<AutomationRule> ?? rules.ToList();
        IReadOnlyDictionary<string, object?> values = Snapshot(sample);
        var steps = new List<AutomationTestResult>();

        AutomationTestResult Step(AutomationEvent @event)
        {
            var step = Run(ruleList, @event, values);
            values = step.Result.Values; // carry settled values forward
            steps.Add(step);
            return step;
        }

        Step(new AutomationEvent(AutomationTrigger.RecordNew));

        if (edits is not null)
        {
            foreach (var (field, value) in edits)
            {
                values = WithEdit(values, field, value);
                Step(new AutomationEvent(AutomationTrigger.FieldChanged, field));
            }
        }

        Step(new AutomationEvent(AutomationTrigger.Validate));
        Step(new AutomationEvent(AutomationTrigger.RecordSave));
        return steps;
    }

    private static Dictionary<string, object?> Snapshot(IReadOnlyDictionary<string, object?> values)
        => new(values, StringComparer.Ordinal);

    private static Dictionary<string, object?> WithEdit(
        IReadOnlyDictionary<string, object?> values,
        string field,
        object? value)
    {
        var copy = Snapshot(values);
        copy[field] = value;
        return copy;
    }
}
