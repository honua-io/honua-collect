// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;

namespace Honua.Field.Core.Automation.Webhooks;

/// <summary>
/// Record-lifecycle events that can trigger webhook deliveries and automation
/// integrations (#127).
/// </summary>
public enum RecordLifecycleEventType
{
    /// <summary>A new field record was created.</summary>
    RecordCreated,

    /// <summary>An existing field record was updated.</summary>
    RecordUpdated,

    /// <summary>A field record was deleted.</summary>
    RecordDeleted,

    /// <summary>A field record finished syncing to the server.</summary>
    RecordSynced,

    /// <summary>A field record's workflow status changed.</summary>
    StatusChanged,

    /// <summary>A media attachment was added to a field record.</summary>
    MediaAdded,
}

/// <summary>Mappings between <see cref="RecordLifecycleEventType"/>, canonical event names, and <see cref="AutomationTrigger"/>.</summary>
public static class RecordLifecycleEventExtensions
{
    /// <summary>Returns the canonical dot-separated webhook event name (e.g. <c>record.created</c>).</summary>
    /// <param name="eventType">The lifecycle event type.</param>
    /// <returns>The canonical event name.</returns>
    public static string ToEventName(this RecordLifecycleEventType eventType) => eventType switch
    {
        RecordLifecycleEventType.RecordCreated => "record.created",
        RecordLifecycleEventType.RecordUpdated => "record.updated",
        RecordLifecycleEventType.RecordDeleted => "record.deleted",
        RecordLifecycleEventType.RecordSynced => "record.synced",
        RecordLifecycleEventType.StatusChanged => "record.status_changed",
        RecordLifecycleEventType.MediaAdded => "record.media_added",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unknown record lifecycle event type."),
    };

    /// <summary>Maps a record-lifecycle event to the corresponding <see cref="AutomationTrigger"/> for the #44 rules engine.</summary>
    /// <param name="eventType">The lifecycle event type.</param>
    /// <returns>The matching automation trigger.</returns>
    public static AutomationTrigger ToAutomationTrigger(this RecordLifecycleEventType eventType) => eventType switch
    {
        RecordLifecycleEventType.RecordCreated => AutomationTrigger.RecordSave,
        RecordLifecycleEventType.RecordUpdated => AutomationTrigger.RecordSave,
        RecordLifecycleEventType.RecordDeleted => AutomationTrigger.RecordDelete,
        RecordLifecycleEventType.RecordSynced => AutomationTrigger.RecordSync,
        RecordLifecycleEventType.StatusChanged => AutomationTrigger.StatusChanged,
        RecordLifecycleEventType.MediaAdded => AutomationTrigger.MediaAdded,
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unknown record lifecycle event type."),
    };
}

/// <summary>
/// A record-lifecycle occurrence routed through automation rules and webhook targets
/// (#127). Carries the record identity, form identity, current (and optional previous)
/// field values, workflow status, actor, tags, and arbitrary metadata.
/// </summary>
public sealed record RecordLifecycleEvent
{
    private readonly IReadOnlyDictionary<string, object?> _values =
        ReadOnlyDictionary<string, object?>.Empty;

    private readonly IReadOnlyDictionary<string, string> _metadata =
        ReadOnlyDictionary<string, string>.Empty;

    private readonly IReadOnlyList<string> _tags = [];

    /// <summary>
    /// Unique identifier for this event occurrence (defaults to a fresh GUID). Used as
    /// the delivery ID and to derive stable idempotency keys per webhook target.
    /// </summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Which lifecycle transition occurred.</summary>
    public required RecordLifecycleEventType EventType { get; init; }

    /// <summary>Identifier of the form / layer schema this record belongs to.</summary>
    public required string FormId { get; init; }

    /// <summary>Identifier of the field record.</summary>
    public required string RecordId { get; init; }

    /// <summary>UTC timestamp when the lifecycle event occurred.</summary>
    public DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Current field values on the record.</summary>
    public IReadOnlyDictionary<string, object?> Values
    {
        get => _values;
        init => _values = value ?? ReadOnlyDictionary<string, object?>.Empty;
    }

    /// <summary>Optional previous field values before the change, used to compute changed fields.</summary>
    public IReadOnlyDictionary<string, object?>? PreviousValues { get; init; }

    /// <summary>Current workflow status of the record, if any.</summary>
    public string? Status { get; init; }

    /// <summary>Previous workflow status before a transition, if any.</summary>
    public string? PreviousStatus { get; init; }

    /// <summary>Optional user or device identifier that initiated the event.</summary>
    public string? ActorId { get; init; }

    /// <summary>Tags currently attached to the record.</summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        init => _tags = value ?? [];
    }

    /// <summary>Additional string metadata included in webhook envelopes.</summary>
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = value ?? ReadOnlyDictionary<string, string>.Empty;
    }

    /// <summary>
    /// Computes the deterministically sorted list of field IDs whose values differ
    /// between <see cref="PreviousValues"/> and <paramref name="effectiveValues"/>.
    /// Returns an empty list when <see cref="PreviousValues"/> is null.
    /// </summary>
    /// <param name="effectiveValues">The post-automation values to compare against <see cref="PreviousValues"/>.</param>
    /// <returns>Sorted list of changed field IDs.</returns>
    public IReadOnlyList<string> GetChangedFields(IReadOnlyDictionary<string, object?>? effectiveValues = null)
    {
        if (PreviousValues is null)
        {
            return [];
        }

        var current = effectiveValues ?? Values;
        var changed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (key, value) in current)
        {
            if (!PreviousValues.TryGetValue(key, out var previous) || !AutomationValue.AreEqual(previous, value))
            {
                changed.Add(key);
            }
        }

        foreach (var key in PreviousValues.Keys)
        {
            if (!current.ContainsKey(key))
            {
                changed.Add(key);
            }
        }

        return [.. changed];
    }
}
