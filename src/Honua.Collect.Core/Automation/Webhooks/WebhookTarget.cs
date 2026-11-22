// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;

namespace Honua.Collect.Core.Automation.Webhooks;

/// <summary>Wire payload format produced for a webhook target (#127).</summary>
public enum WebhookPayloadFormat
{
    /// <summary>
    /// Structured JSON envelope containing event metadata (<c>event</c>, <c>event_id</c>,
    /// <c>occurred_at</c>, <c>form_id</c>, <c>record_id</c>, <c>status</c>, <c>changed_fields</c>,
    /// <c>tags</c>, <c>metadata</c>) and record fields nested under <c>data</c>.
    /// </summary>
    HonuaEnvelope,

    /// <summary>
    /// Flat JSON object placing record fields alongside top-level event metadata so
    /// no-code webhook catch hooks (such as Zapier or Make) can map fields directly
    /// without nested path expressions.
    /// </summary>
    FlatWebhook,
}

/// <summary>
/// A configured webhook or external automation target (#127). Targets may subscribe
/// directly to <see cref="Events"/> (optionally filtered by <see cref="FormIdFilter"/>
/// and a #44 <see cref="Condition"/>) and/or be invoked explicitly by a
/// <see cref="DispatchWebhookAction"/> from an <see cref="AutomationRule"/>.
/// </summary>
public sealed record WebhookTarget
{
    private readonly IReadOnlyList<RecordLifecycleEventType> _events = [];

    private readonly IReadOnlyDictionary<string, string> _headers =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Unique stable identifier for this target (used in idempotency keys and rule actions).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name for authoring and diagnostics.</summary>
    public required string Name { get; init; }

    /// <summary>Destination webhook URL (must be HTTPS, or loopback for local development).</summary>
    public required string Url { get; init; }

    /// <summary>Whether deliveries to this target are enabled. Defaults to <see langword="true"/>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Lifecycle events this target subscribes to automatically. Leave empty if the
    /// target should only be invoked via explicit <see cref="DispatchWebhookAction"/> rules.
    /// </summary>
    public IReadOnlyList<RecordLifecycleEventType> Events
    {
        get => _events;
        init => _events = value ?? [];
    }

    /// <summary>Optional form ID filter; when set, only events for this form match subscriptions.</summary>
    public string? FormIdFilter { get; init; }

    /// <summary>
    /// Optional condition guard (reusing #44's <see cref="RuleCondition"/> or
    /// <see cref="ExpressionCondition"/>) evaluated against the record's values.
    /// </summary>
    public IRuleCondition? Condition { get; init; }

    /// <summary>Payload format sent to this target. Defaults to <see cref="WebhookPayloadFormat.HonuaEnvelope"/>.</summary>
    public WebhookPayloadFormat PayloadFormat { get; init; } = WebhookPayloadFormat.HonuaEnvelope;

    /// <summary>
    /// Optional shared signing secret. When non-empty, the router signs the payload with
    /// HMAC-SHA256 and attaches <c>X-Honua-Signature-256</c>.
    /// </summary>
    public string? SigningSecret { get; init; }

    /// <summary>HTTP method to use (defaults to <c>POST</c>).</summary>
    public string HttpMethod { get; init; } = "POST";

    /// <summary>
    /// Optional extra HTTP headers (such as API keys or bearer tokens). Reserved
    /// <c>X-Honua-*</c> and <c>Idempotency-Key</c> headers are managed by the router
    /// and transport and cannot be overridden.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers
    {
        get => _headers;
        init => _headers = value ?? ReadOnlyDictionary<string, string>.Empty;
    }
}
