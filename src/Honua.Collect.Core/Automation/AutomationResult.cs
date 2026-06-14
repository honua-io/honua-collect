namespace Honua.Collect.Core.Automation;

/// <summary>An alert surfaced by an automation run.</summary>
/// <param name="Severity">Message severity.</param>
/// <param name="Message">Message text.</param>
/// <param name="RuleName">The rule that raised it.</param>
public sealed record AutomationAlert(AutomationSeverity Severity, string Message, string RuleName);

/// <summary>A validation failure raised by an automation run.</summary>
/// <param name="Message">Why the record is invalid.</param>
/// <param name="RuleName">The rule that raised it.</param>
public sealed record AutomationValidationError(string Message, string RuleName);

/// <summary>An HTTP request queued offline by an automation run, for replay on sync.</summary>
/// <param name="Url">Destination URL.</param>
/// <param name="Body">Optional request body.</param>
/// <param name="RuleName">The rule that queued it.</param>
public sealed record QueuedRequest(string Url, string? Body, string RuleName);

/// <summary>
/// The outcome of running automation rules for one event: the field values after
/// any set-field actions, plus the alerts, validation errors, and offline-queued
/// requests the rules produced, and which rules fired.
/// </summary>
public sealed record AutomationResult
{
    /// <summary>The record's values after the run (set-field actions applied).</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    /// <summary>Alerts raised, in order.</summary>
    public IReadOnlyList<AutomationAlert> Alerts { get; init; } = [];

    /// <summary>Validation errors raised (non-empty means save should be blocked).</summary>
    public IReadOnlyList<AutomationValidationError> ValidationErrors { get; init; } = [];

    /// <summary>Requests queued for offline replay.</summary>
    public IReadOnlyList<QueuedRequest> QueuedRequests { get; init; } = [];

    /// <summary>Names of the rules that fired, in order.</summary>
    public IReadOnlyList<string> FiredRules { get; init; } = [];

    /// <summary>Whether the record passed automation validation (no validation errors).</summary>
    public bool IsValid => ValidationErrors.Count == 0;
}
