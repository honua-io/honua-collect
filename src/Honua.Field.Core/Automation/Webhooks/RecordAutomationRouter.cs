// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Automation.Http;
using Honua.Field.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Field.Core.Automation.Webhooks;

/// <summary>Why a webhook target was skipped during routing.</summary>
public enum WebhookSkipReason
{
    /// <summary>The target is disabled.</summary>
    Disabled,

    /// <summary>The target does not subscribe to this event type and no rule dispatched it.</summary>
    UnmatchedEvent,

    /// <summary>The target's <see cref="WebhookTarget.FormIdFilter"/> did not match the record's form.</summary>
    UnmatchedForm,

    /// <summary>The target's <see cref="WebhookTarget.Condition"/> evaluated to false.</summary>
    UnmatchedCondition,

    /// <summary>The target's URL is invalid or uses cleartext HTTP to a non-loopback host.</summary>
    InsecureOrInvalidUrl,

    /// <summary>A rule's <see cref="DispatchWebhookAction"/> referenced a target ID not present in the configured targets.</summary>
    UnknownRuleTarget,
}

/// <summary>A webhook target that was evaluated and not enqueued, with the reason why.</summary>
/// <param name="TargetId">The target's identifier.</param>
/// <param name="Reason">Why delivery was skipped.</param>
public sealed record SkippedWebhookTarget(string TargetId, WebhookSkipReason Reason);

/// <summary>A webhook delivery enqueued into the durable <see cref="HttpRequestOutbox"/>.</summary>
/// <param name="TargetId">The target's identifier.</param>
/// <param name="EntryId">The outbox entry identifier.</param>
/// <param name="IdempotencyKey">The stable idempotency key assigned to the delivery.</param>
/// <param name="EventName">The emitted webhook event name.</param>
/// <param name="RuleName">The automation rule that triggered delivery, or null for direct subscriptions.</param>
public sealed record EnqueuedWebhookDelivery(
    string TargetId,
    string EntryId,
    string IdempotencyKey,
    string EventName,
    string? RuleName);

/// <summary>Outcome of routing a <see cref="RecordLifecycleEvent"/> through rules and webhook targets.</summary>
public sealed record RecordAutomationRouteResult
{
    /// <summary>Result of the #44 automation rules run, if rules were supplied.</summary>
    public AutomationResult? AutomationResult { get; init; }

    /// <summary>Webhook deliveries enqueued into the durable HTTP outbox.</summary>
    public IReadOnlyList<EnqueuedWebhookDelivery> EnqueuedDeliveries { get; init; } = [];

    /// <summary>Targets (or unknown rule targets) that were skipped, with reasons.</summary>
    public IReadOnlyList<SkippedWebhookTarget> SkippedTargets { get; init; } = [];

    /// <summary>Number of additional direct <see cref="HttpRequestAction"/> entries enqueued from rules.</summary>
    public int DirectHttpRequestsEnqueued { get; init; }

    /// <summary>Total number of outbox entries produced by this routing pass.</summary>
    public int TotalEnqueued => EnqueuedDeliveries.Count + DirectHttpRequestsEnqueued;
}

/// <summary>
/// Routes record-lifecycle events through the #44 <see cref="AutomationRuntime"/> and
/// dispatches matching webhook/integration targets into the durable
/// <see cref="HttpRequestOutbox"/> (#127).
/// </summary>
/// <remarks>
/// <para>
/// Routing is completely offline-safe: no network call happens here. Matching targets
/// are serialized (in <see cref="WebhookPayloadFormat.HonuaEnvelope"/> or
/// <see cref="WebhookPayloadFormat.FlatWebhook"/> format), signed with HMAC-SHA256 when
/// a secret is configured, assigned a deterministic idempotency key
/// (<c>wh:&lt;targetId&gt;:&lt;eventId&gt;</c>), and durably enqueued in the outbox to replay
/// when connectivity returns.
/// </para>
/// </remarks>
public sealed class RecordAutomationRouter
{
    private readonly HttpRequestOutbox _outbox;
    private readonly AutomationRuntime _runtime;
    private readonly TimeProvider _clock;
    private readonly ILogger<RecordAutomationRouter> _logger;

    /// <summary>Creates a router backed by the given durable HTTP outbox.</summary>
    /// <param name="outbox">Durable HTTP outbox where webhook deliveries are enqueued.</param>
    /// <param name="runtime">Optional #44 automation runtime (defaults to a fresh instance).</param>
    /// <param name="clock">Optional clock for signing timestamps (defaults to system UTC).</param>
    /// <param name="logger">Optional structured logger.</param>
    public RecordAutomationRouter(
        HttpRequestOutbox outbox,
        AutomationRuntime? runtime = null,
        TimeProvider? clock = null,
        ILogger<RecordAutomationRouter>? logger = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _runtime = runtime ?? new AutomationRuntime();
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RecordAutomationRouter>.Instance;
    }

    /// <summary>
    /// Evaluates optional automation rules and all configured webhook targets for a
    /// record-lifecycle event, enqueuing every matching delivery into the outbox.
    /// </summary>
    /// <param name="event">The record-lifecycle event.</param>
    /// <param name="targets">Configured webhook/integration targets.</param>
    /// <param name="rules">Optional #44 automation rules to run before evaluating targets.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of automation effects, enqueued deliveries, and skipped targets.</returns>
    public async Task<RecordAutomationRouteResult> RouteAsync(
        RecordLifecycleEvent @event,
        IReadOnlyList<WebhookTarget> targets,
        IReadOnlyList<AutomationRule>? rules = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(targets);

        AutomationResult? automationResult = null;
        var directHttpEnqueued = 0;

        if (rules is { Count: > 0 })
        {
            var trigger = @event.EventType.ToAutomationTrigger();
            automationResult = _runtime.Run(rules, new AutomationEvent(trigger), @event.Values);
            directHttpEnqueued = await _outbox.EnqueueFromAsync(automationResult, ct).ConfigureAwait(false);
        }

        var effectiveValues = automationResult?.Values ?? @event.Values;
        var effectiveTags = MergeTags(@event.Tags, automationResult?.Tags);
        var changedFields = @event.GetChangedFields(effectiveValues);
        var defaultEventName = @event.EventType.ToEventName();

        // Index explicit DispatchWebhookAction requests by target ID (first rule wins per target).
        var ruleDispatches = new Dictionary<string, QueuedWebhookDispatch>(StringComparer.Ordinal);
        if (automationResult is not null)
        {
            foreach (var dispatch in automationResult.WebhookDispatches)
            {
                ruleDispatches.TryAdd(dispatch.TargetId, dispatch);
            }
        }

        var knownTargetIds = new HashSet<string>(StringComparer.Ordinal);
        var enqueued = new List<EnqueuedWebhookDelivery>();
        var skipped = new List<SkippedWebhookTarget>();

        foreach (var target in targets)
        {
            knownTargetIds.Add(target.Id);
            ruleDispatches.TryGetValue(target.Id, out var ruleDispatch);

            if (!target.Enabled)
            {
                skipped.Add(new SkippedWebhookTarget(target.Id, WebhookSkipReason.Disabled));
                continue;
            }

            if (ruleDispatch is null)
            {
                var skipReason = EvaluateSubscription(target, @event, effectiveValues);
                if (skipReason is not null)
                {
                    skipped.Add(new SkippedWebhookTarget(target.Id, skipReason.Value));
                    continue;
                }
            }

            if (!IsValidSecureUrl(target.Url))
            {
                _logger.LogWarning(
                    "Skipping webhook target {TargetId}: URL is invalid or uses cleartext HTTP to a non-loopback host.",
                    target.Id);
                skipped.Add(new SkippedWebhookTarget(target.Id, WebhookSkipReason.InsecureOrInvalidUrl));
                continue;
            }

            var eventName = string.IsNullOrWhiteSpace(ruleDispatch?.EventType)
                ? defaultEventName
                : ruleDispatch.EventType!;

            var effectiveEvent = @event.OccurredAtUtc == default
                ? @event with { OccurredAtUtc = _clock.GetUtcNow() }
                : @event;

            var mergedMetadata = MergeMetadata(@event.Metadata, ruleDispatch?.ExtraMetadata);
            var payload = WebhookPayloadSerializer.Serialize(
                effectiveEvent,
                eventName,
                effectiveValues,
                effectiveTags,
                changedFields,
                mergedMetadata,
                target.PayloadFormat);

            var timestamp = _clock.GetUtcNow().ToUnixTimeSeconds();
            var headers = BuildHeaders(target, @event.EventId, eventName, timestamp, payload);
            var idempotencyKey = $"wh:{target.Id}:{@event.EventId}";

            var request = new HttpOutboxRequest
            {
                Method = string.IsNullOrWhiteSpace(target.HttpMethod) ? "POST" : target.HttpMethod,
                Url = target.Url,
                Body = payload,
                Headers = headers,
                IdempotencyKey = idempotencyKey,
            };

            var entry = await _outbox.EnqueueAsync(request, ruleDispatch?.RuleName ?? target.Name, ct)
                .ConfigureAwait(false);

            enqueued.Add(new EnqueuedWebhookDelivery(
                target.Id,
                entry.Id,
                idempotencyKey,
                eventName,
                ruleDispatch?.RuleName));
        }

        // Report any DispatchWebhookAction that pointed at an unknown target ID.
        foreach (var targetId in ruleDispatches.Keys)
        {
            if (!knownTargetIds.Contains(targetId))
            {
                skipped.Add(new SkippedWebhookTarget(targetId, WebhookSkipReason.UnknownRuleTarget));
            }
        }

        return new RecordAutomationRouteResult
        {
            AutomationResult = automationResult,
            EnqueuedDeliveries = enqueued,
            SkippedTargets = skipped,
            DirectHttpRequestsEnqueued = directHttpEnqueued,
        };
    }

    private static WebhookSkipReason? EvaluateSubscription(
        WebhookTarget target,
        RecordLifecycleEvent @event,
        IReadOnlyDictionary<string, object?> effectiveValues)
    {
        if (!target.Events.Contains(@event.EventType))
        {
            return WebhookSkipReason.UnmatchedEvent;
        }

        if (target.FormIdFilter is not null
            && !string.Equals(target.FormIdFilter, @event.FormId, StringComparison.Ordinal))
        {
            return WebhookSkipReason.UnmatchedForm;
        }

        if (target.Condition is not null && !target.Condition.Matches(effectiveValues))
        {
            return WebhookSkipReason.UnmatchedCondition;
        }

        return null;
    }

    private static bool IsValidSecureUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            && EndpointSecurity.IsTransportSecure(uri);

    private static Dictionary<string, string> BuildHeaders(
        WebhookTarget target,
        string eventId,
        string eventName,
        long timestamp,
        string payload)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawName, value) in target.Headers)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var name = rawName.Trim();
            if (IsReservedHeader(name))
            {
                continue;
            }

            headers[name] = value;
        }

        headers["Content-Type"] = "application/json";
        headers[WebhookHeaders.Event] = eventName;
        headers[WebhookHeaders.Delivery] = eventId;
        headers[WebhookHeaders.Timestamp] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrEmpty(target.SigningSecret))
        {
            headers[WebhookHeaders.Signature256] = WebhookSigner.Sign(target.SigningSecret, timestamp, payload);
        }

        return headers;
    }

    private static bool IsReservedHeader(string name)
        => name.StartsWith("X-Honua-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HttpClientRequestTransport.IdempotencyKeyHeader, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> MergeTags(IReadOnlyList<string> eventTags, IReadOnlyList<string>? ruleTags)
    {
        if (ruleTags is null || ruleTags.Count == 0)
        {
            return eventTags;
        }

        var merged = new List<string>(eventTags.Count + ruleTags.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in eventTags)
        {
            if (seen.Add(tag))
            {
                merged.Add(tag);
            }
        }

        foreach (var tag in ruleTags)
        {
            if (seen.Add(tag))
            {
                merged.Add(tag);
            }
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string> eventMetadata,
        IReadOnlyDictionary<string, string>? ruleMetadata)
    {
        if (ruleMetadata is null || ruleMetadata.Count == 0)
        {
            return eventMetadata;
        }

        var merged = new Dictionary<string, string>(eventMetadata, StringComparer.Ordinal);
        foreach (var (key, value) in ruleMetadata)
        {
            merged[key] = value;
        }

        return merged;
    }
}
