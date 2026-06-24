using Honua.Collect.Core.Field.Forms;

namespace Honua.Collect.Core.Automation;

/// <summary>
/// Wires the <see cref="AutomationRuntime"/> into a live <see cref="FormSession"/>
/// so Data Events actually fire at the right lifecycle points (BACKLOG #44). It
/// listens for value changes and exposes explicit hooks for new/load/validate/save,
/// runs the matching rules through the runtime, and folds set-field / computed /
/// AI-action writes back into the session via <see cref="FormSession.SetValue"/>.
/// Alerts, validation errors, tags, queued requests/notifications, follow-ups, and
/// AI invocations from the latest run are exposed for the host UI to surface.
/// </summary>
/// <remarks>
/// Everything is offline and deterministic. Because set-field writes go back
/// through <see cref="FormSession.SetValue"/>, they also re-run the form's own
/// calculated fields/visibility — and the runtime's own loop guard plus the
/// session's "same value is a no-op" rule keep a field-set that re-triggers from
/// spinning.
/// </remarks>
public sealed class AutomationSession : IDisposable
{
    private readonly FormSession _session;
    private readonly IReadOnlyList<AutomationRule> _rules;
    private readonly AutomationRuntime _runtime;
    private bool _applying;

    private AutomationSession(FormSession session, IReadOnlyList<AutomationRule> rules, AutomationRuntime runtime)
    {
        _session = session;
        _rules = rules;
        _runtime = runtime;
        _session.FieldChanged += OnFieldChanged;
    }

    /// <summary>The outcome of the most recent automation run (alerts, errors, queued items, tags…).</summary>
    public AutomationResult? LastResult { get; private set; }

    /// <summary>
    /// Attaches automation to a form session. Optionally pass a runtime to supply a
    /// live AI-action provider; the default uses the no-op stub.
    /// </summary>
    /// <param name="session">The live form session.</param>
    /// <param name="rules">The authored rules.</param>
    /// <param name="runtime">Optional runtime; defaults to one with the no-op AI provider.</param>
    /// <returns>The attached automation session.</returns>
    public static AutomationSession Attach(
        FormSession session,
        IReadOnlyList<AutomationRule> rules,
        AutomationRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rules);
        return new AutomationSession(session, rules, runtime ?? new AutomationRuntime());
    }

    /// <summary>Fires the <see cref="AutomationTrigger.RecordNew"/> event (call after creating a new record).</summary>
    /// <returns>The run result.</returns>
    public AutomationResult RaiseRecordNew() => Run(new AutomationEvent(AutomationTrigger.RecordNew));

    /// <summary>Fires the <see cref="AutomationTrigger.RecordLoad"/> event (call after opening an existing record).</summary>
    /// <returns>The run result.</returns>
    public AutomationResult RaiseRecordLoad() => Run(new AutomationEvent(AutomationTrigger.RecordLoad));

    /// <summary>Fires the <see cref="AutomationTrigger.Validate"/> event.</summary>
    /// <returns>The run result; <see cref="AutomationResult.IsValid"/> reflects automation validation.</returns>
    public AutomationResult RaiseValidate() => Run(new AutomationEvent(AutomationTrigger.Validate));

    /// <summary>Fires the <see cref="AutomationTrigger.RecordSave"/> event.</summary>
    /// <returns>The run result.</returns>
    public AutomationResult RaiseRecordSave() => Run(new AutomationEvent(AutomationTrigger.RecordSave));

    private void OnFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        // Re-entrancy guard: applying a rule's writes raises FieldChanged again;
        // the runtime already cascades field changes itself, so do not recurse.
        if (_applying)
        {
            return;
        }

        Run(new AutomationEvent(AutomationTrigger.FieldChanged, e.FieldId));
    }

    private AutomationResult Run(AutomationEvent @event)
    {
        var result = _runtime.Run(_rules, @event, Snapshot());
        Apply(result);
        LastResult = result;
        return result;
    }

    private IReadOnlyDictionary<string, object?> Snapshot()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in _session.Fields)
        {
            values[field.FieldId] = field.Value;
        }

        return values;
    }

    private void Apply(AutomationResult result)
    {
        _applying = true;
        try
        {
            foreach (var (fieldId, value) in result.Values)
            {
                // Only write fields the session actually has; unknown keys (e.g. a
                // computed scratch field) are left in the result for the host.
                if (HasField(fieldId))
                {
                    _session.SetValue(fieldId, value);
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private bool HasField(string fieldId)
    {
        foreach (var field in _session.Fields)
        {
            if (string.Equals(field.FieldId, fieldId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Detaches from the session's change event.</summary>
    public void Dispose() => _session.FieldChanged -= OnFieldChanged;
}
