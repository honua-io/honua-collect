// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Forms.Cascade;
using Honua.Collect.Core.Field.Related;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Forms.Expressions;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Forms;

public sealed class FormSession : ICaptureHost
{
    private readonly Dictionary<string, FieldState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FieldState> _ordered = [];
    private readonly Dictionary<string, RepeatGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChoiceCascadeRule> _cascades = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecordLinkBehavior> _linkBehaviors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RelatedRecords> _related = new(StringComparer.OrdinalIgnoreCase);
    private readonly FormDefinition _scalarForm;

    private FormSession(
        FormDefinition form,
        FieldRecord record,
        IReadOnlyDictionary<string, RepeatBounds>? repeatBounds = null,
        IEnumerable<ChoiceCascadeRule>? cascadeRules = null,
        Localization.FormLocalization? localization = null,
        IReadOnlyDictionary<string, RecordLinkBehavior>? linkBehaviors = null)
    {
        // When a localization service is supplied, the runtime binds to
        // the form localized to the active language. Field ids, types, validation,
        // and logic are untouched, so the localized form captures identically.
        Localization = localization;
        form = localization?.Localize(form) ?? form;

        Form = form;
        Record = record;

        if (cascadeRules is not null)
        {
            foreach (var rule in cascadeRules)
            {
                _cascades[rule.FieldId] = rule;
            }
        }

        if (linkBehaviors is not null)
        {
            foreach (var behavior in linkBehaviors)
            {
                _linkBehaviors[behavior.Key] = behavior.Value;
            }
        }

        // Split scalar sections (flat fields) from repeatable sections (groups of
        // rows). The SDK helpers operate on the scalar form so repeat-section
        // fields are never validated or calculated at the top level.
        var scalarSections = form.Sections.Where(s => !s.Repeatable).ToList();
        _scalarForm = form with { Sections = scalarSections };

        foreach (var section in scalarSections)
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

        foreach (var section in form.Sections.Where(s => s.Repeatable))
        {
            var bounds = repeatBounds is not null && repeatBounds.TryGetValue(section.SectionId, out var b) ? b : null;
            _groups[section.SectionId] = new RepeatGroup(form, section, RepeatGroup.ReadRows(record, section.SectionId), bounds);
        }

        RecomputeScalar();
    }

    public FormDefinition Form { get; }

    public Localization.FormLocalization? Localization { get; }

    public string? ActiveLanguage => Localization?.ActiveLanguage;

    public FieldRecord Record { get; }

    public IReadOnlyList<FieldState> Fields => _ordered;

    public IEnumerable<FieldState> VisibleFields => _ordered.Where(f => f.IsVisible);

    public IReadOnlyCollection<RepeatGroup> RepeatGroups => _groups.Values;

    public event EventHandler<FieldChangedEventArgs>? FieldChanged;

    public RepeatGroup GetRepeat(string sectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        return _groups.TryGetValue(sectionId, out var group)
            ? group
            : throw new KeyNotFoundException($"Form '{Form.FormId}' has no repeatable section '{sectionId}'.");
    }

    public IEnumerable<string> RelatedFields => _ordered
        .Where(s => s.Field.Type == FormFieldType.RecordLink)
        .Select(s => s.FieldId);

    public RelatedRecords GetRelated(string fieldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);

        if (_related.TryGetValue(fieldId, out var existing))
        {
            return existing;
        }

        var state = GetField(fieldId);
        if (state.Field.Type != FormFieldType.RecordLink)
        {
            throw new ArgumentException(
                $"Field '{fieldId}' is {state.Field.Type}, not a RecordLink field.", nameof(fieldId));
        }

        var behavior = _linkBehaviors.TryGetValue(fieldId, out var b) ? b : RecordLinkBehavior.Cascade;
        var related = new RelatedRecords(Record, state.Field, behavior);
        _related[fieldId] = related;
        return related;
    }

    public static FormSession Open(
        FormDefinition form,
        FieldRecord record,
        IReadOnlyDictionary<string, RepeatBounds>? repeatBounds = null,
        IEnumerable<ChoiceCascadeRule>? cascadeRules = null,
        Localization.FormLocalization? localization = null,
        IReadOnlyDictionary<string, RecordLinkBehavior>? linkBehaviors = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(record);
        return new FormSession(form, record, repeatBounds, cascadeRules, localization, linkBehaviors);
    }

    public static FormSession CreateForNewRecord(
        FormDefinition form,
        string recordId,
        FieldRecord? seedFrom = null,
        IEnumerable<string>? seedFieldIds = null,
        IReadOnlyDictionary<string, RepeatBounds>? repeatBounds = null,
        IEnumerable<ChoiceCascadeRule>? cascadeRules = null,
        IReadOnlyDictionary<string, object?>? seedDefaults = null,
        Localization.FormLocalization? localization = null,
        IReadOnlyDictionary<string, RecordLinkBehavior>? linkBehaviors = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        var record = new FieldRecord { RecordId = recordId, FormId = form.FormId };

        if (seedFrom is not null)
        {
            SeedDefaults(form, record, seedFrom, seedFieldIds);
        }

        if (seedDefaults is not null)
        {
            ApplyResolvedDefaults(form, record, seedDefaults);
        }

        return new FormSession(form, record, repeatBounds, cascadeRules, localization, linkBehaviors);
    }

    public FieldState GetField(string fieldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        return _states.TryGetValue(fieldId, out var state)
            ? state
            : throw new KeyNotFoundException($"Form '{Form.FormId}' has no field '{fieldId}'.");
    }

    public object? GetValue(string fieldId) => GetField(fieldId).Value;

    public void SetValue(string fieldId, object? value)
    {
        var state = GetField(fieldId);
        if (Equals(state.Value, value))
        {
            return;
        }

        state.Value = value;
        Record.Values[fieldId] = value;
        RecomputeScalar();
        FieldChanged?.Invoke(this, new FieldChangedEventArgs(fieldId));
    }

    public void AddMedia(CapturedMediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var fieldId = attachment.FieldId
            ?? throw new ArgumentException("Attachment must specify a FieldId to bind to a field.", nameof(attachment));

        var state = GetField(fieldId);
        state.AddMedia(attachment);
        SyncMediaIntoRecord();
        RecomputeScalar();
        FieldChanged?.Invoke(this, new FieldChangedEventArgs(fieldId));
    }

    public bool RemoveMedia(string fieldId, string attachmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        var state = GetField(fieldId);
        if (!state.RemoveMedia(attachmentId))
        {
            return false;
        }

        SyncMediaIntoRecord();
        RecomputeScalar();
        FieldChanged?.Invoke(this, new FieldChangedEventArgs(fieldId));
        return true;
    }

    public RepeatInstance AddRepeatInstance(string sectionId) => GetRepeat(sectionId).AddInstance();

    public FormValidationResult Validate()
    {
        var errors = new List<FormValidationError>(RecomputeScalar().Errors);

        foreach (var group in _groups.Values)
        {
            group.PersistInto(Record);

            // Row-count bounds (min/max repeats) are enforced at the section level
            // . Errors are keyed to the section id so the UI can
            // surface them on the repeat group header rather than a single row.
            if (group.Bounds is { } bounds)
            {
                if (bounds.Min is { } min && group.Instances.Count < min)
                {
                    errors.Add(new FormValidationError(
                        group.SectionId,
                        $"Add at least {min} {(min == 1 ? "entry" : "entries")}."));
                }

                if (bounds.Max is { } max && group.Instances.Count > max)
                {
                    errors.Add(new FormValidationError(
                        group.SectionId,
                        $"At most {max} {(max == 1 ? "entry" : "entries")} allowed."));
                }
            }

            for (var index = 0; index < group.Instances.Count; index++)
            {
                foreach (var error in group.Instances[index].Validate().Errors)
                {
                    errors.Add(new FormValidationError($"{group.SectionId}[{index}].{error.FieldId}", error.Message));
                }
            }
        }

        return new FormValidationResult(errors);
    }

    public bool CanSubmit => Validate().IsValid;

    public FormValidationResult MarkReadyToSubmit(DateTimeOffset? transitionTimeUtc = null)
        => TransitionWhenValid(RecordStatus.ReadyToSubmit, transitionTimeUtc);

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

    private FormValidationResult RecomputeScalar()
    {
        CalculatedFieldEvaluator.ApplyCalculatedFields(_scalarForm, Record);

        // Mirror calculated outputs back into the bound state.
        foreach (var state in _ordered)
        {
            if (Record.Values.TryGetValue(state.FieldId, out var current))
            {
                state.Value = current;
            }
        }

        RecomputeVisibility();
        RecomputeChoices();

        var validation = FormValidator.Validate(_scalarForm, Record);

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

    private void RecomputeChoices()
    {
        foreach (var state in _ordered)
        {
            _cascades.TryGetValue(state.FieldId, out var rule);
            var available = ChoiceFilter.Available(state.Field, rule, Record.Values);
            state.AvailableChoices = available;

            // A cascading select whose chosen value is no longer offered (because
            // the parent changed) is cleared so the record never carries a stale,
            // now-invalid child selection.
            if (rule is not null && !ChoiceFilter.IsStillValid(state.Value, available))
            {
                state.Value = null;
                Record.Values[state.FieldId] = null;
            }
        }
    }

    private bool ResolveVisibility(string fieldId, HashSet<string> resolving)
    {
        if (!_states.TryGetValue(fieldId, out var state))
        {
            return false;
        }

        // A boolean relevance expression (e.g. "${a}='x' and ${b}>5") supersedes
        // the single-comparison visibility rule when present.
        var relevance = state.Field.RelevanceExpression;
        if (!string.IsNullOrWhiteSpace(relevance))
        {
            return ExpressionEvaluator.EvaluateBoolean(relevance, Record.Values);
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

        // Only scalar sections seed flatly; repeat rows are not carried forward.
        foreach (var field in form.Sections.Where(s => !s.Repeatable).SelectMany(s => s.Fields))
        {
            // Never seed computed fields (they are recalculated) or media fields
            // (attachments are host-local and not reusable across records).
            if (field.Type is FormFieldType.Calculated || FormFieldTypes.IsMedia(field.Type))
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

    private static void ApplyResolvedDefaults(
        FormDefinition form,
        FieldRecord target,
        IReadOnlyDictionary<string, object?> defaults)
    {
        foreach (var field in form.Sections.Where(s => !s.Repeatable).SelectMany(s => s.Fields))
        {
            if (field.Type is FormFieldType.Calculated || FormFieldTypes.IsMedia(field.Type))
            {
                continue;
            }

            if (defaults.TryGetValue(field.FieldId, out var value) && value is not null)
            {
                target.Values[field.FieldId] = value;
            }
        }
    }

}

public sealed record FieldChangedEventArgs(string FieldId);
