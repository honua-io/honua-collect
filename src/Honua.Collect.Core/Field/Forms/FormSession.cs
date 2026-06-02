using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Forms;

/// <summary>
/// A live, stateful field-capture session over an SDK <see cref="FormDefinition"/>
/// and <see cref="FieldRecord"/>. This is the platform-neutral "form runtime"
/// every capture widget binds to and the piece the SDK deliberately leaves to
/// the product: the SDK ships immutable form/record contracts plus stateless
/// helpers (validation, calculated fields, workflow), but nothing that holds the
/// evolving state of a form being filled in.
/// </summary>
/// <remarks>
/// <para>Responsibilities, mapped to the Survey123/Fulcrum parity backlog:</para>
/// <list type="bullet">
///   <item>Live field visibility, including cascading dependencies (BACKLOG F3).</item>
///   <item>Live calculated fields and live, per-field validation.</item>
///   <item>Submit readiness and SDK workflow transitions.</item>
///   <item>Per-field media capture management (backs widgets C1–C6).</item>
///   <item>Default-from-previous / "favorites" seeding (BACKLOG F5).</item>
/// </list>
/// <para>
/// Repeatable sections are modelled in <see cref="FieldState.RepeatInstance"/>
/// but only a single instance is materialised today; full repeat expansion
/// requires the SDK record contract to carry nested values and is tracked
/// separately.
/// </para>
/// </remarks>
public sealed class FormSession
{
    private readonly Dictionary<string, FieldState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FieldState> _ordered = [];

    private FormSession(FormDefinition form, FieldRecord record)
    {
        Form = form;
        Record = record;

        foreach (var section in form.Sections)
        {
            foreach (var field in section.Fields)
            {
                var state = new FieldState(field, section, repeatInstance: 0);
                record.Values.TryGetValue(field.FieldId, out var seeded);
                state.Value = seeded;
                _states[field.FieldId] = state;
                _ordered.Add(state);
            }
        }

        Recompute();
    }

    /// <summary>The form definition being captured.</summary>
    public FormDefinition Form { get; }

    /// <summary>
    /// The underlying SDK record. Its <see cref="FieldRecord.Values"/> and
    /// <see cref="FieldRecord.Media"/> are kept in sync as the session changes,
    /// so it can be persisted as a draft or handed to sync directly.
    /// </summary>
    public FieldRecord Record { get; }

    /// <summary>All field states in form order.</summary>
    public IReadOnlyList<FieldState> Fields => _ordered;

    /// <summary>Field states that are currently visible, in form order.</summary>
    public IEnumerable<FieldState> VisibleFields => _ordered.Where(f => f.IsVisible);

    /// <summary>Raised after a value change has been applied and state recomputed.</summary>
    public event EventHandler<FieldChangedEventArgs>? FieldChanged;

    /// <summary>Opens a session over an existing record (for example, a saved draft).</summary>
    /// <param name="form">Form definition.</param>
    /// <param name="record">Existing record whose values seed the session.</param>
    /// <returns>A session bound to <paramref name="record"/>.</returns>
    public static FormSession Open(FormDefinition form, FieldRecord record)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(record);
        return new FormSession(form, record);
    }

    /// <summary>Starts a session for a brand-new record.</summary>
    /// <param name="form">Form definition.</param>
    /// <param name="recordId">Identifier for the new record.</param>
    /// <param name="seedFrom">
    /// Optional previous record to copy values from (default-from-previous /
    /// "favorites", BACKLOG F5).
    /// </param>
    /// <param name="seedFieldIds">
    /// When <paramref name="seedFrom"/> is supplied, restricts seeding to these
    /// fields. <see langword="null"/> seeds every non-calculated, non-media field.
    /// </param>
    /// <returns>A session for a new draft record.</returns>
    public static FormSession CreateForNewRecord(
        FormDefinition form,
        string recordId,
        FieldRecord? seedFrom = null,
        IEnumerable<string>? seedFieldIds = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        var record = new FieldRecord { RecordId = recordId, FormId = form.FormId };

        if (seedFrom is not null)
        {
            SeedDefaults(form, record, seedFrom, seedFieldIds);
        }

        return new FormSession(form, record);
    }

    /// <summary>Gets the live state for a field.</summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <returns>The field state.</returns>
    public FieldState GetField(string fieldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        return _states.TryGetValue(fieldId, out var state)
            ? state
            : throw new KeyNotFoundException($"Form '{Form.FormId}' has no field '{fieldId}'.");
    }

    /// <summary>Gets the current value of a field.</summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <returns>The current value, or <see langword="null"/>.</returns>
    public object? GetValue(string fieldId) => GetField(fieldId).Value;

    /// <summary>
    /// Sets a field value and recomputes calculated fields, visibility, and
    /// validation. Setting the same value is a no-op and does not raise
    /// <see cref="FieldChanged"/>.
    /// </summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <param name="value">New value.</param>
    public void SetValue(string fieldId, object? value)
    {
        var state = GetField(fieldId);
        if (Equals(state.Value, value))
        {
            return;
        }

        state.Value = value;
        Record.Values[fieldId] = value;
        Recompute();
        FieldChanged?.Invoke(this, new FieldChangedEventArgs(fieldId));
    }

    /// <summary>Adds a captured media attachment to a field.</summary>
    /// <param name="attachment">Captured media with its host-local path.</param>
    public void AddMedia(CapturedMediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var fieldId = attachment.FieldId
            ?? throw new ArgumentException("Attachment must specify a FieldId to bind to a field.", nameof(attachment));

        var state = GetField(fieldId);
        state.AddMedia(attachment);
        SyncMediaIntoRecord();
        Recompute();
        FieldChanged?.Invoke(this, new FieldChangedEventArgs(fieldId));
    }

    /// <summary>Removes a captured media attachment from a field.</summary>
    /// <param name="fieldId">Field the attachment is bound to.</param>
    /// <param name="attachmentId">Attachment identifier to remove.</param>
    /// <returns><see langword="true"/> if an attachment was removed.</returns>
    public bool RemoveMedia(string fieldId, string attachmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        var state = GetField(fieldId);
        if (!state.RemoveMedia(attachmentId))
        {
            return false;
        }

        SyncMediaIntoRecord();
        Recompute();
        FieldChanged?.Invoke(this, new FieldChangedEventArgs(fieldId));
        return true;
    }

    /// <summary>
    /// Re-applies calculated fields, recomputes visibility and validation, and
    /// returns the result restricted to currently visible fields.
    /// </summary>
    /// <returns>Validation result for the visible portion of the form.</returns>
    public FormValidationResult Validate() => Recompute();

    /// <summary>Whether the form is currently complete and valid for submission.</summary>
    public bool CanSubmit => Validate().IsValid;

    /// <summary>
    /// Validates and, if valid, transitions the record to
    /// <see cref="RecordStatus.ReadyToSubmit"/>.
    /// </summary>
    /// <param name="transitionTimeUtc">Optional transition timestamp.</param>
    /// <returns>The validation result; the transition only occurs when valid.</returns>
    public FormValidationResult MarkReadyToSubmit(DateTimeOffset? transitionTimeUtc = null)
        => TransitionWhenValid(RecordStatus.ReadyToSubmit, transitionTimeUtc);

    /// <summary>
    /// Validates and, if valid, transitions the record to
    /// <see cref="RecordStatus.Submitted"/>.
    /// </summary>
    /// <param name="transitionTimeUtc">Optional transition timestamp.</param>
    /// <returns>The validation result; the transition only occurs when valid.</returns>
    public FormValidationResult Submit(DateTimeOffset? transitionTimeUtc = null)
        => TransitionWhenValid(RecordStatus.Submitted, transitionTimeUtc);

    private FormValidationResult TransitionWhenValid(RecordStatus target, DateTimeOffset? transitionTimeUtc)
    {
        var result = Validate();
        if (result.IsValid)
        {
            RecordWorkflow.Transition(Record, target, transitionTimeUtc);
        }

        return result;
    }

    private FormValidationResult Recompute()
    {
        CalculatedFieldEvaluator.ApplyCalculatedFields(Form, Record);

        // Mirror calculated outputs back into the bound state.
        foreach (var state in _ordered)
        {
            if (Record.Values.TryGetValue(state.FieldId, out var current))
            {
                state.Value = current;
            }
        }

        RecomputeVisibility();

        var validation = FormValidator.Validate(Form, Record);

        // The SDK validator uses flat visibility; restrict reported errors to
        // fields this session considers visible so cascaded-hidden fields never
        // surface phantom "required" errors the user can't act on.
        var visibleErrors = validation.Errors
            .Where(error => _states.TryGetValue(error.FieldId, out var s) && s.IsVisible)
            .ToList();

        foreach (var state in _ordered)
        {
            state.SetErrors(visibleErrors
                .Where(e => string.Equals(e.FieldId, state.FieldId, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Message));
        }

        return new FormValidationResult(visibleErrors);
    }

    private void RecomputeVisibility()
    {
        var resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in _ordered)
        {
            state.IsVisible = ResolveVisibility(state.FieldId, resolving);
        }
    }

    private bool ResolveVisibility(string fieldId, HashSet<string> resolving)
    {
        if (!_states.TryGetValue(fieldId, out var state))
        {
            return false;
        }

        var rule = state.Field.VisibilityRule;
        if (rule is null)
        {
            return true;
        }

        // Guard against circular visibility dependencies.
        if (!resolving.Add(fieldId))
        {
            return false;
        }

        try
        {
            // A field is hidden if the field it depends on is itself hidden,
            // so visibility cascades through chains (A controls B controls C).
            if (_states.ContainsKey(rule.DependsOnFieldId) &&
                !ResolveVisibility(rule.DependsOnFieldId, resolving))
            {
                return false;
            }

            Record.Values.TryGetValue(rule.DependsOnFieldId, out var actual);
            return Compare(actual, rule.MatchValue, rule.Operator);
        }
        finally
        {
            resolving.Remove(fieldId);
        }
    }

    private static bool Compare(object? actual, object? expected, ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equals => FieldValues.AreEqual(actual, expected),
        ComparisonOperator.NotEquals => !FieldValues.AreEqual(actual, expected),
        ComparisonOperator.GreaterThan => FieldValues.TryAsDouble(actual, out var a) && FieldValues.TryAsDouble(expected, out var b) && a > b,
        ComparisonOperator.LessThan => FieldValues.TryAsDouble(actual, out var c) && FieldValues.TryAsDouble(expected, out var d) && c < d,
        ComparisonOperator.Contains => FieldValues.ToText(actual).Contains(FieldValues.ToText(expected), StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private void SyncMediaIntoRecord()
    {
        Record.Media.Clear();
        foreach (var state in _ordered)
        {
            foreach (var attachment in state.Media)
            {
                Record.Media.Add(attachment.ToSdkAttachment());
            }
        }
    }

    private static void SeedDefaults(
        FormDefinition form,
        FieldRecord target,
        FieldRecord source,
        IEnumerable<string>? seedFieldIds)
    {
        var allow = seedFieldIds is null ? null : new HashSet<string>(seedFieldIds, StringComparer.OrdinalIgnoreCase);

        foreach (var field in form.Sections.SelectMany(s => s.Fields))
        {
            // Never seed computed fields (they are recalculated) or media fields
            // (attachments are host-local and not reusable across records).
            if (field.Type is FormFieldType.Calculated || IsMediaField(field.Type))
            {
                continue;
            }

            if (allow is not null && !allow.Contains(field.FieldId))
            {
                continue;
            }

            if (source.Values.TryGetValue(field.FieldId, out var value) && value is not null)
            {
                target.Values[field.FieldId] = value;
            }
        }
    }

    private static bool IsMediaField(FormFieldType type)
        => type is FormFieldType.Photo or FormFieldType.Video or FormFieldType.Audio
            or FormFieldType.Signature or FormFieldType.Sketch or FormFieldType.File;
}

/// <summary>Event data for <see cref="FormSession.FieldChanged"/>.</summary>
/// <param name="FieldId">Identifier of the field whose value or media changed.</param>
public sealed record FieldChangedEventArgs(string FieldId);
