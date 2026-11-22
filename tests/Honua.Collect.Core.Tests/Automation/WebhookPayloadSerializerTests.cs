// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Honua.Collect.Core.Automation.Webhooks;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>Tests for deterministic webhook JSON serialization and changed-field diffing (#127).</summary>
public class WebhookPayloadSerializerTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 11, 21, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Envelope_format_serializes_metadata_and_sorted_typed_values()
    {
        var ev = new RecordLifecycleEvent
        {
            EventId = "evt-001",
            EventType = RecordLifecycleEventType.RecordUpdated,
            FormId = "hydrant-inspection",
            RecordId = "rec-42",
            OccurredAtUtc = OccurredAt,
            Status = "needs_repair",
            PreviousStatus = "open",
            ActorId = "inspector-7",
            Tags = ["urgent"],
            PreviousValues = new Dictionary<string, object?>
            {
                ["condition"] = "Fair",
                ["removed_field"] = "old",
                ["unchanged"] = 5,
            },
            Values = new Dictionary<string, object?>
            {
                ["z_note"] = "leaking valve",
                ["condition"] = "Poor",
                ["unchanged"] = 5,
                ["pressure_psi"] = 62.5d,
                ["count_int"] = 3,
                ["count_long"] = 9_000_000_000L,
                ["ratio_float"] = 1.5f,
                ["cost_dec"] = 12.34m,
                ["flagged"] = true,
                ["cleared"] = null,
                ["inspected_dto"] = OccurredAt,
                ["inspected_dt"] = new DateTime(2026, 11, 21, 18, 0, 0, DateTimeKind.Utc),
                ["nested"] = new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 },
                ["items"] = new object?[] { "x", 2, null },
                ["custom_guid"] = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            },
            Metadata = new Dictionary<string, string> { ["region"] = "west" },
        };

        var changed = ev.GetChangedFields();
        var json = WebhookPayloadSerializer.Serialize(
            ev,
            ev.EventType.ToEventName(),
            ev.Values,
            ev.Tags,
            changed,
            ev.Metadata,
            WebhookPayloadFormat.HonuaEnvelope);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("record.updated", root.GetProperty("event").GetString());
        Assert.Equal("evt-001", root.GetProperty("event_id").GetString());
        Assert.Equal("hydrant-inspection", root.GetProperty("form_id").GetString());
        Assert.Equal("rec-42", root.GetProperty("record_id").GetString());
        Assert.Equal("needs_repair", root.GetProperty("status").GetString());
        Assert.Equal("open", root.GetProperty("previous_status").GetString());
        Assert.Equal("inspector-7", root.GetProperty("actor_id").GetString());
        Assert.Equal("west", root.GetProperty("metadata").GetProperty("region").GetString());

        var changedList = root.GetProperty("changed_fields").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("condition", changedList);
        Assert.Contains("removed_field", changedList);
        Assert.DoesNotContain("unchanged", changedList);

        var data = root.GetProperty("data");
        Assert.Equal("Poor", data.GetProperty("condition").GetString());
        Assert.Equal(62.5d, data.GetProperty("pressure_psi").GetDouble());
        Assert.Equal(3, data.GetProperty("count_int").GetInt32());
        Assert.Equal(9_000_000_000L, data.GetProperty("count_long").GetInt64());
        Assert.Equal(12.34m, data.GetProperty("cost_dec").GetDecimal());
        Assert.True(data.GetProperty("flagged").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("cleared").ValueKind);
        Assert.Equal(1, data.GetProperty("nested").GetProperty("a").GetInt32());
        Assert.Equal(3, data.GetProperty("items").GetArrayLength());
        Assert.Equal("11111111-2222-3333-4444-555555555555", data.GetProperty("custom_guid").GetString());
    }

    [Fact]
    public void Flat_format_flattens_fields_and_prefixes_reserved_key_collisions()
    {
        var ev = new RecordLifecycleEvent
        {
            EventId = "evt-002",
            EventType = RecordLifecycleEventType.RecordCreated,
            FormId = "pole-audit",
            RecordId = "rec-99",
            OccurredAtUtc = OccurredAt,
            Status = "submitted",
            Values = new Dictionary<string, object?>
            {
                ["condition"] = "Poor",
                ["status"] = "corroded", // collides with reserved top-level key
                ["event"] = "storm-2026", // collides with reserved top-level key
            },
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "field-app",
            },
        };

        var json = WebhookPayloadSerializer.Serialize(
            ev,
            ev.EventType.ToEventName(),
            ev.Values,
            ev.Tags,
            ev.GetChangedFields(),
            ev.Metadata,
            WebhookPayloadFormat.FlatWebhook);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("record.created", root.GetProperty("event").GetString());
        Assert.Equal("submitted", root.GetProperty("status").GetString());
        Assert.Equal("Poor", root.GetProperty("condition").GetString());
        Assert.Equal("corroded", root.GetProperty("field_status").GetString());
        Assert.Equal("storm-2026", root.GetProperty("field_event").GetString());
        Assert.Equal("field-app", root.GetProperty("meta_source").GetString());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.False(root.TryGetProperty("previous_status", out _));
        Assert.False(root.TryGetProperty("actor_id", out _));
    }

    [Fact]
    public void Lifecycle_event_extensions_map_all_enum_values_and_reject_invalid()
    {
        foreach (var eventType in Enum.GetValues<RecordLifecycleEventType>())
        {
            Assert.StartsWith("record.", eventType.ToEventName(), StringComparison.Ordinal);
            _ = eventType.ToAutomationTrigger();
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ((RecordLifecycleEventType)999).ToEventName());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((RecordLifecycleEventType)999).ToAutomationTrigger());
    }

    [Fact]
    public void Handles_non_finite_numbers_value_type_collections_and_flat_key_collisions()
    {
        var ev = new RecordLifecycleEvent
        {
            EventId = "evt-edge",
            EventType = RecordLifecycleEventType.RecordUpdated,
            FormId = "metrics",
            RecordId = "rec-edge",
            OccurredAtUtc = OccurredAt,
            Status = "open",
            Values = new Dictionary<string, object?>
            {
                ["nan_d"] = double.NaN,
                ["inf_d"] = double.PositiveInfinity,
                ["nan_f"] = float.NaN,
                ["inf_f"] = float.NegativeInfinity,
                ["int_array"] = new[] { 10, 20, 30 },
                ["double_list"] = new List<double> { 1.5, 2.5 },
                ["status"] = "field-status-value",
                ["field_status"] = "literal-field-status-value",
                ["meta_env"] = "record-meta-env-value",
            },
            Metadata = new Dictionary<string, string>
            {
                ["env"] = "prod",
            },
        };

        var json = WebhookPayloadSerializer.Serialize(
            ev,
            ev.EventType.ToEventName(),
            ev.Values,
            ev.Tags,
            ev.GetChangedFields(),
            ev.Metadata,
            WebhookPayloadFormat.FlatWebhook);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("nan_d").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("inf_d").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nan_f").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("inf_f").ValueKind);
        Assert.Equal([10, 20, 30], root.GetProperty("int_array").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal([1.5, 2.5], root.GetProperty("double_list").EnumerateArray().Select(e => e.GetDouble()).ToArray());

        // No duplicate property names: collisions are deterministically suffixed.
        Assert.Equal("open", root.GetProperty("status").GetString());
        Assert.Equal("literal-field-status-value", root.GetProperty("field_status").GetString());
        Assert.Equal("field-status-value", root.GetProperty("field_status_2").GetString());
        Assert.Equal("record-meta-env-value", root.GetProperty("meta_env").GetString());
        Assert.Equal("prod", root.GetProperty("meta_env_2").GetString());
    }
}
