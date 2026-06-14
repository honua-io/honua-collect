namespace Honua.Collect.Core.Automation;

/// <summary>Base type for the actions an automation rule can perform.</summary>
public abstract record AutomationAction;

/// <summary>Sets a field to a value (computed/derived fill).</summary>
/// <param name="FieldId">The field to set.</param>
/// <param name="Value">The value to assign.</param>
public sealed record SetFieldAction(string FieldId, object? Value) : AutomationAction;

/// <summary>Raises a non-blocking alert / message to the user.</summary>
/// <param name="Severity">Message severity.</param>
/// <param name="Message">Message text.</param>
public sealed record AlertAction(AutomationSeverity Severity, string Message) : AutomationAction;

/// <summary>Fails validation with a message (blocks save when raised on a validate/save trigger).</summary>
/// <param name="Message">Why the record is invalid.</param>
public sealed record InvalidateAction(string Message) : AutomationAction;

/// <summary>
/// Queues an HTTP request to be sent when connectivity returns (BACKLOG #44 —
/// "HTTP request, queued offline"). The runtime records it; it is replayed by the
/// sync layer, so the automation stays fully offline-capable.
/// </summary>
/// <param name="Url">Destination URL.</param>
/// <param name="Body">Optional request body.</param>
public sealed record QueueRequestAction(string Url, string? Body = null) : AutomationAction;
