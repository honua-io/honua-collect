namespace Honua.Collect.Core.Automation;

/// <summary>
/// When an automation rule fires (BACKLOG, #44 — Fulcrum-style offline Data
/// Events). Triggers correspond to points in the record lifecycle.
/// </summary>
public enum AutomationTrigger
{
    /// <summary>A new record is started.</summary>
    RecordNew,

    /// <summary>An existing record is loaded for editing.</summary>
    RecordLoad,

    /// <summary>A field's value changed.</summary>
    FieldChanged,

    /// <summary>The record is being validated / saved.</summary>
    Validate,

    /// <summary>The record is being saved.</summary>
    RecordSave,
}

/// <summary>Severity of an alert raised by an automation action.</summary>
public enum AutomationSeverity
{
    /// <summary>Informational message.</summary>
    Info,

    /// <summary>A warning the user should see but can proceed past.</summary>
    Warning,

    /// <summary>An error condition.</summary>
    Error,
}

/// <summary>The comparison a <see cref="RuleCondition"/> applies.</summary>
public enum ConditionOperator
{
    /// <summary>Field value equals the comparand.</summary>
    Equals,

    /// <summary>Field value differs from the comparand.</summary>
    NotEquals,

    /// <summary>Field has a (non-missing) value.</summary>
    Exists,

    /// <summary>Field has no value.</summary>
    NotExists,

    /// <summary>Field value, as a number, is greater than the comparand.</summary>
    GreaterThan,

    /// <summary>Field value, as a number, is less than the comparand.</summary>
    LessThan,
}
