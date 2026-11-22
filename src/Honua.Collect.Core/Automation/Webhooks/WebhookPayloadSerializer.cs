// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Honua.Collect.Core.Automation.Webhooks;

/// <summary>
/// Deterministic JSON serializer for webhook payloads (#127). Supports both the
/// structured <see cref="WebhookPayloadFormat.HonuaEnvelope"/> and the flat
/// <see cref="WebhookPayloadFormat.FlatWebhook"/> format for no-code catch hooks.
/// </summary>
public static class WebhookPayloadSerializer
{
    private static readonly HashSet<string> ReservedFlatKeys = new(StringComparer.Ordinal)
    {
        "event",
        "event_id",
        "occurred_at",
        "form_id",
        "record_id",
        "status",
        "previous_status",
        "actor_id",
        "changed_fields",
        "tags",
    };

    /// <summary>Serializes a record-lifecycle event into a deterministic JSON string.</summary>
    /// <param name="event">The triggering record-lifecycle event.</param>
    /// <param name="eventName">Canonical or rule-overridden event name.</param>
    /// <param name="effectiveValues">Effective field values (including any automation writes).</param>
    /// <param name="effectiveTags">Effective tags (including any automation tags).</param>
    /// <param name="changedFields">Sorted list of changed field IDs.</param>
    /// <param name="metadata">Merged event and rule metadata.</param>
    /// <param name="format">Target wire payload format.</param>
    /// <returns>UTF-8 JSON payload string.</returns>
    public static string Serialize(
        RecordLifecycleEvent @event,
        string eventName,
        IReadOnlyDictionary<string, object?> effectiveValues,
        IReadOnlyList<string> effectiveTags,
        IReadOnlyList<string> changedFields,
        IReadOnlyDictionary<string, string> metadata,
        WebhookPayloadFormat format)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(effectiveValues);
        ArgumentNullException.ThrowIfNull(effectiveTags);
        ArgumentNullException.ThrowIfNull(changedFields);
        ArgumentNullException.ThrowIfNull(metadata);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (format == WebhookPayloadFormat.FlatWebhook)
            {
                WriteFlat(writer, @event, eventName, effectiveValues, effectiveTags, changedFields, metadata);
            }
            else
            {
                WriteEnvelope(writer, @event, eventName, effectiveValues, effectiveTags, changedFields, metadata);
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteEnvelope(
        Utf8JsonWriter writer,
        RecordLifecycleEvent @event,
        string eventName,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> changedFields,
        IReadOnlyDictionary<string, string> metadata)
    {
        writer.WriteStartObject();
        WriteCommonMetadata(writer, @event, eventName, tags, changedFields);

        writer.WritePropertyName("data");
        writer.WriteStartObject();
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }

        writer.WriteEndObject();

        writer.WritePropertyName("metadata");
        writer.WriteStartObject();
        foreach (var (key, value) in metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteFlat(
        Utf8JsonWriter writer,
        RecordLifecycleEvent @event,
        string eventName,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> changedFields,
        IReadOnlyDictionary<string, string> metadata)
    {
        writer.WriteStartObject();
        WriteCommonMetadata(writer, @event, eventName, tags, changedFields);

        var emitted = new HashSet<string>(ReservedFlatKeys, StringComparer.Ordinal);

        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var baseName = ReservedFlatKeys.Contains(key) ? $"field_{key}" : key;
            var propertyName = AllocateUniqueName(baseName, emitted);
            writer.WritePropertyName(propertyName);
            WriteValue(writer, value);
        }

        foreach (var (key, value) in metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var propertyName = AllocateUniqueName($"meta_{key}", emitted);
            writer.WriteString(propertyName, value);
        }

        writer.WriteEndObject();
    }

    private static string AllocateUniqueName(string baseName, HashSet<string> emitted)
    {
        if (emitted.Add(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"{baseName}_{suffix}";
            if (emitted.Add(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static void WriteCommonMetadata(
        Utf8JsonWriter writer,
        RecordLifecycleEvent @event,
        string eventName,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> changedFields)
    {
        writer.WriteString("event", eventName);
        writer.WriteString("event_id", @event.EventId);
        writer.WriteString("occurred_at", @event.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("form_id", @event.FormId);
        writer.WriteString("record_id", @event.RecordId);

        if (@event.Status is not null)
        {
            writer.WriteString("status", @event.Status);
        }

        if (@event.PreviousStatus is not null)
        {
            writer.WriteString("previous_status", @event.PreviousStatus);
        }

        if (@event.ActorId is not null)
        {
            writer.WriteString("actor_id", @event.ActorId);
        }

        writer.WritePropertyName("changed_fields");
        writer.WriteStartArray();
        foreach (var field in changedFields)
        {
            writer.WriteStringValue(field);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        foreach (var tag in tags)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case float f:
                if (float.IsFinite(f))
                {
                    writer.WriteNumberValue(f);
                }
                else
                {
                    writer.WriteNullValue();
                }

                break;
            case double d:
                if (double.IsFinite(d))
                {
                    writer.WriteNumberValue(d);
                }
                else
                {
                    writer.WriteNullValue();
                }

                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                var utc = dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    : dt.ToUniversalTime();
                writer.WriteStringValue(utc.ToString("O", CultureInfo.InvariantCulture));
                break;
            case IReadOnlyDictionary<string, object?> dict:
                writer.WriteStartObject();
                foreach (var (k, v) in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(k);
                    WriteValue(writer, v);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
