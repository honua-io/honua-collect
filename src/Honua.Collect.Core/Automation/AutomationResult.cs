using Honua.Collect.Core.Automation.Http;

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
/// A richer HTTP request queued by a <see cref="HttpRequestAction"/> for durable
/// offline replay through the <see cref="HttpRequestOutbox"/> — carries the full
/// request (method/headers/body/idempotency key), unlike the lightweight
/// <see cref="QueuedRequest"/>.
/// </summary>
/// <param name="Request">The request to enqueue.</param>
/// <param name="RuleName">The rule that queued it.</param>
public sealed record QueuedHttpRequest(HttpOutboxRequest Request, string RuleName);

/// <summary>A notification enqueued offline by an automation run, for delivery by the host.</summary>
/// <param name="Title">Notification title.</param>
/// <param name="Body">Notification body.</param>
/// <param name="RuleName">The rule that enqueued it.</param>
public sealed record QueuedNotification(string Title, string Body, string RuleName);

/// <summary>A follow-up task scheduled by an automation run.</summary>
/// <param name="Description">What the follow-up is for.</param>
/// <param name="DueInDays">How many days out the follow-up is due.</param>
/// <param name="RuleName">The rule that scheduled it.</param>
public sealed record ScheduledFollowUp(string Description, int DueInDays, string RuleName);

/// <summary>A record that an AI action was invoked, so the seam is observable in results/tests.</summary>
/// <param name="ActionId">The AI action that was invoked.</param>
/// <param name="Handled">Whether a provider serviced it (false for the no-op stub).</param>
/// <param name="RuleName">The rule that invoked it.</param>
public sealed record AiActionInvocation(string ActionId, bool Handled, string RuleName);

/// <summary>
/// The outcome of running automation rules for one event: the field values after
/// any set-field/compute/AI actions, plus the alerts, validation errors, tags,
/// offline-queued requests/notifications, scheduled follow-ups, and AI-action
/// invocations the rules produced, and which rules fired.
/// </summary>
public sealed record AutomationResult
{
    /// <summary>The record's values after the run (set-field actions applied).</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    /// <summary>Alerts raised, in order.</summary>
    public IReadOnlyList<AutomationAlert> Alerts { get; init; } = [];

    /// <summary>Validation errors raised (non-empty means save should be blocked).</summary>
    public IReadOnlyList<AutomationValidationError> ValidationErrors { get; init; } = [];

    /// <summary>Lightweight requests queued for offline replay (url/body only).</summary>
    public IReadOnlyList<QueuedRequest> QueuedRequests { get; init; } = [];

    /// <summary>Durable HTTP requests queued for the outbox (method/headers/body/idempotency key).</summary>
    public IReadOnlyList<QueuedHttpRequest> HttpRequests { get; init; } = [];

    /// <summary>Platform-neutral open-URL intents the host should launch.</summary>
    public IReadOnlyList<OpenUrlIntent> OpenUrlIntents { get; init; } = [];

    /// <summary>Notifications enqueued for offline delivery.</summary>
    public IReadOnlyList<QueuedNotification> Notifications { get; init; } = [];

    /// <summary>Follow-up tasks scheduled by the run.</summary>
    public IReadOnlyList<ScheduledFollowUp> FollowUps { get; init; } = [];

    /// <summary>Tags added to the record, in order, deduplicated.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>AI-action invocations, in order — observable even with the no-op stub.</summary>
    public IReadOnlyList<AiActionInvocation> AiInvocations { get; init; } = [];

    /// <summary>Names of the rules that fired, in order.</summary>
    public IReadOnlyList<string> FiredRules { get; init; } = [];

    /// <summary>Whether the record passed automation validation (no validation errors).</summary>
    public bool IsValid => ValidationErrors.Count == 0;
}
