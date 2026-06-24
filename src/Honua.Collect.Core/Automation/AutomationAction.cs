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

/// <summary>
/// Sets a field to the result of an SDK form-expression (BACKLOG #44 — "compute").
/// Unlike <see cref="SetFieldAction"/> (a literal value), the value is derived at
/// run time from the record's current fields using the same expression engine that
/// drives calculated fields and visibility — e.g. <c>${length} * ${width}</c>. A
/// null/failed evaluation leaves the field cleared.
/// </summary>
/// <param name="FieldId">The field to set.</param>
/// <param name="Expression">Expression source evaluated against the record's values.</param>
public sealed record ComputeFieldAction(string FieldId, string Expression) : AutomationAction;

/// <summary>Adds a tag to the record (idempotent — re-adding an existing tag is a no-op).</summary>
/// <param name="Tag">The tag to add.</param>
public sealed record AddTagAction(string Tag) : AutomationAction;

/// <summary>
/// Enqueues an offline notification for the user (BACKLOG #44). Like queued HTTP,
/// it is recorded by the runtime and delivered/replayed by the host, so the rule
/// stays offline-capable.
/// </summary>
/// <param name="Title">Notification title.</param>
/// <param name="Body">Notification body.</param>
public sealed record EnqueueNotificationAction(string Title, string Body) : AutomationAction;

/// <summary>
/// Schedules a follow-up task a number of days out (BACKLOG #44 — "schedule a
/// follow-up"). Recorded offline; the host surfaces it as a reminder/task.
/// </summary>
/// <param name="Description">What the follow-up is for.</param>
/// <param name="DueInDays">How many days from the run the follow-up is due.</param>
public sealed record ScheduleFollowUpAction(string Description, int DueInDays) : AutomationAction;

/// <summary>
/// Invokes an on-device AI action as an automation step (BACKLOG #44 — the
/// differentiator Fulcrum lacks; e.g. "if the photo shows corrosion, set
/// condition=Poor"). The runtime resolves the named action through the
/// <see cref="IAiActionProvider"/> seam and folds the field writes it returns back
/// into the record. With no provider wired (the default), this is a no-op stub:
/// the seam is exercised but nothing runs, and a live provider
/// (Bedrock/OpenAI, per the server AI providers) is the deferred follow-up.
/// </summary>
/// <param name="ActionId">Identifier of the registered AI action to invoke.</param>
/// <param name="Prompt">Optional prompt/instruction passed to the action.</param>
public sealed record RunAiAction(string ActionId, string? Prompt = null) : AutomationAction;
