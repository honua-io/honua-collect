// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Honua.Collect.Core.Automation;
using Honua.Collect.Core.Automation.Http;
using Honua.Collect.Core.Automation.Webhooks;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>
/// End-to-end tests for record-lifecycle automation and webhook/integration routing (#127):
/// extends the #44 data-events engine and durable HTTP outbox with lifecycle triggers,
/// target filtering, HMAC-SHA256 signing, Zapier-friendly flat payloads, idempotent
/// offline queueing, and replay through <see cref="HttpClientRequestTransport"/>.
/// </summary>
public class RecordAutomationRouterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public bool Online { get; set; } = true;
        public List<(HttpRequestMessage Request, string Body)> Delivered { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!Online)
            {
                throw new HttpRequestException("Offline");
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Delivered.Add((request, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task End_to_end_offline_routing_signs_enqueues_and_replays_when_online()
    {
        var clock = new FakeClock(Now);
        var store = new InMemoryHttpOutboxStore();
        var handler = new FakeHttpHandler { Online = false };
        using var client = new HttpClient(handler);
        var transport = new HttpClientRequestTransport(client);
        var outbox = new HttpRequestOutbox(store, transport, clock);
        var router = new RecordAutomationRouter(outbox, clock: clock);

        const string secret = "whsec_live_test_key";
        var targets = new[]
        {
            new WebhookTarget
            {
                Id = "zapier-ops",
                Name = "Zapier Critical Triage",
                Url = "https://hooks.zapier.example/hooks/catch/1/abc",
                Events = [RecordLifecycleEventType.RecordCreated, RecordLifecycleEventType.RecordUpdated],
                FormIdFilter = "pole-inspection",
                Condition = new ExpressionCondition("${condition} = 'Poor'"),
                PayloadFormat = WebhookPayloadFormat.FlatWebhook,
                SigningSecret = secret,
                Headers = new Dictionary<string, string>
                {
                    ["X-Custom-Tenant"] = "utility-north",
                    ["X-Honua-Event"] = "spoofed.event", // reserved header must not override
                    ["Idempotency-Key"] = "spoofed-key", // reserved header must not override
                },
            },
        };

        var rules = new[]
        {
            new AutomationRule
            {
                Name = "flag-poor-condition",
                Trigger = AutomationTrigger.RecordSave,
                Condition = new RuleCondition("condition", ConditionOperator.Equals, "Poor"),
                Actions =
                [
                    new SetFieldAction("priority", "P1"),
                    new AddTagAction("escalated"),
                ],
            },
        };

        var ev = new RecordLifecycleEvent
        {
            EventId = "evt-777",
            EventType = RecordLifecycleEventType.RecordCreated,
            FormId = "pole-inspection",
            RecordId = "rec-100",
            OccurredAtUtc = Now,
            Status = "submitted",
            Values = new Dictionary<string, object?>
            {
                ["condition"] = "Poor",
                ["pole_id"] = "P-404",
            },
            PreviousValues = new Dictionary<string, object?>
            {
                ["condition"] = "Fair",
                ["pole_id"] = "P-404",
            },
            Tags = ["field"],
        };

        var route = await router.RouteAsync(ev, targets, rules);

        Assert.NotNull(route.AutomationResult);
        Assert.Equal(["flag-poor-condition"], route.AutomationResult.FiredRules);
        var delivery = Assert.Single(route.EnqueuedDeliveries);
        Assert.Equal("zapier-ops", delivery.TargetId);
        Assert.Equal("wh:zapier-ops:evt-777", delivery.IdempotencyKey);
        Assert.Equal("record.created", delivery.EventName);
        Assert.Empty(route.SkippedTargets);

        // Drain while offline -> stays pending.
        var offlineDrain = await outbox.DrainAsync();
        Assert.Equal(0, offlineDrain.Sent);
        Assert.Equal(1, offlineDrain.Retried);
        Assert.Empty(handler.Delivered);

        // Reconnect and advance past backoff -> replays cleanly.
        handler.Online = true;
        clock.Advance(TimeSpan.FromMinutes(2));
        var onlineDrain = await outbox.DrainAsync();
        Assert.Equal(1, onlineDrain.Sent);

        var (sentReq, sentBody) = Assert.Single(handler.Delivered);
        Assert.Equal("wh:zapier-ops:evt-777", Assert.Single(sentReq.Headers.GetValues("Idempotency-Key")));
        Assert.Equal("record.created", Assert.Single(sentReq.Headers.GetValues(WebhookHeaders.Event)));
        Assert.Equal("evt-777", Assert.Single(sentReq.Headers.GetValues(WebhookHeaders.Delivery)));
        Assert.Equal("utility-north", Assert.Single(sentReq.Headers.GetValues("X-Custom-Tenant")));

        var tsHeader = long.Parse(Assert.Single(sentReq.Headers.GetValues(WebhookHeaders.Timestamp)));
        var sigHeader = Assert.Single(sentReq.Headers.GetValues(WebhookHeaders.Signature256));
        Assert.True(WebhookSigner.Verify(secret, tsHeader, sentBody, sigHeader));

        using var doc = JsonDocument.Parse(sentBody);
        var root = doc.RootElement;
        Assert.Equal("P1", root.GetProperty("priority").GetString());
        Assert.Equal("P-404", root.GetProperty("pole_id").GetString());
        var tags = root.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(["field", "escalated"], tags);
        var changed = root.GetProperty("changed_fields").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(["condition", "priority"], changed);
    }

    [Fact]
    public async Task Filters_disabled_unmatched_and_insecure_targets_and_reports_skip_reasons()
    {
        var store = new InMemoryHttpOutboxStore();
        var handler = new FakeHttpHandler();
        using var client = new HttpClient(handler);
        var outbox = new HttpRequestOutbox(store, new HttpClientRequestTransport(client));
        var router = new RecordAutomationRouter(outbox);

        var targets = new[]
        {
            new WebhookTarget
            {
                Id = "t-disabled",
                Name = "Disabled",
                Url = "https://example.test/disabled",
                Enabled = false,
                Events = [RecordLifecycleEventType.RecordSynced],
            },
            new WebhookTarget
            {
                Id = "t-wrong-event",
                Name = "Wrong Event",
                Url = "https://example.test/event",
                Events = [RecordLifecycleEventType.RecordDeleted],
            },
            new WebhookTarget
            {
                Id = "t-wrong-form",
                Name = "Wrong Form",
                Url = "https://example.test/form",
                Events = [RecordLifecycleEventType.RecordSynced],
                FormIdFilter = "other-form",
            },
            new WebhookTarget
            {
                Id = "t-wrong-cond",
                Name = "Wrong Condition",
                Url = "https://example.test/cond",
                Events = [RecordLifecycleEventType.RecordSynced],
                Condition = new RuleCondition("severity", ConditionOperator.GreaterThan, 5),
            },
            new WebhookTarget
            {
                Id = "t-insecure",
                Name = "Insecure HTTP",
                Url = "http://remote.example.test/hook",
                Events = [RecordLifecycleEventType.RecordSynced],
            },
            new WebhookTarget
            {
                Id = "t-file-scheme",
                Name = "File Scheme",
                Url = "file:///etc/hosts",
                Events = [RecordLifecycleEventType.RecordSynced],
            },
            new WebhookTarget
            {
                Id = "t-good",
                Name = "Good Target",
                Url = "https://example.test/good",
                Events = [RecordLifecycleEventType.RecordSynced],
            },
        };

        var ev = new RecordLifecycleEvent
        {
            EventId = "evt-filter",
            EventType = RecordLifecycleEventType.RecordSynced,
            FormId = "bridge-check",
            RecordId = "rec-1",
            OccurredAtUtc = Now,
            Values = new Dictionary<string, object?> { ["severity"] = 2 },
        };

        var result = await router.RouteAsync(ev, targets);

        Assert.Single(result.EnqueuedDeliveries, d => d.TargetId == "t-good");
        Assert.Equal(6, result.SkippedTargets.Count);
        Assert.Contains(new SkippedWebhookTarget("t-disabled", WebhookSkipReason.Disabled), result.SkippedTargets);
        Assert.Contains(new SkippedWebhookTarget("t-wrong-event", WebhookSkipReason.UnmatchedEvent), result.SkippedTargets);
        Assert.Contains(new SkippedWebhookTarget("t-wrong-form", WebhookSkipReason.UnmatchedForm), result.SkippedTargets);
        Assert.Contains(new SkippedWebhookTarget("t-wrong-cond", WebhookSkipReason.UnmatchedCondition), result.SkippedTargets);
        Assert.Contains(new SkippedWebhookTarget("t-insecure", WebhookSkipReason.InsecureOrInvalidUrl), result.SkippedTargets);
        Assert.Contains(new SkippedWebhookTarget("t-file-scheme", WebhookSkipReason.InsecureOrInvalidUrl), result.SkippedTargets);
    }

    [Fact]
    public async Task Rule_dispatch_webhook_action_routes_target_overrides_event_merges_metadata_and_flags_unknown_target()
    {
        var store = new InMemoryHttpOutboxStore();
        var handler = new FakeHttpHandler();
        using var client = new HttpClient(handler);
        var outbox = new HttpRequestOutbox(store, new HttpClientRequestTransport(client));
        var router = new RecordAutomationRouter(outbox);

        var targets = new[]
        {
            new WebhookTarget
            {
                Id = "pager",
                Name = "On-Call Pager",
                Url = "https://pager.example.test/trigger",
                Events = [], // Not subscribed directly; invoked only via rule DispatchWebhookAction
                PayloadFormat = WebhookPayloadFormat.HonuaEnvelope,
            },
        };

        var rules = new[]
        {
            new AutomationRule
            {
                Name = "escalate-status",
                Trigger = AutomationTrigger.StatusChanged,
                Actions =
                [
                    new DispatchWebhookAction(
                        "pager",
                        EventType: "record.escalated",
                        ExtraMetadata: new Dictionary<string, string> { ["queue"] = "tier1" }),
                    new DispatchWebhookAction("missing-target"),
                    HttpRequestAction.Post("https://audit.example.test/log", """{"ok":true}""", "direct-1"),
                ],
            },
        };

        var ev = new RecordLifecycleEvent
        {
            EventId = "evt-rule-1",
            EventType = RecordLifecycleEventType.StatusChanged,
            FormId = "valve-check",
            RecordId = "rec-7",
            OccurredAtUtc = Now,
            Status = "escalated",
            PreviousStatus = "open",
            Metadata = new Dictionary<string, string> { ["env"] = "prod" },
        };

        var result = await router.RouteAsync(ev, targets, rules);

        var delivery = Assert.Single(result.EnqueuedDeliveries);
        Assert.Equal("pager", delivery.TargetId);
        Assert.Equal("record.escalated", delivery.EventName);
        Assert.Equal("escalate-status", delivery.RuleName);
        Assert.Equal(1, result.DirectHttpRequestsEnqueued);
        Assert.Equal(2, result.TotalEnqueued);
        Assert.Contains(new SkippedWebhookTarget("missing-target", WebhookSkipReason.UnknownRuleTarget), result.SkippedTargets);

        var outboxEntry = (await store.LoadAllAsync()).Single(e => e.Request.IdempotencyKey == "wh:pager:evt-rule-1");
        using var doc = JsonDocument.Parse(outboxEntry.Request.Body!);
        Assert.Equal("record.escalated", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("prod", doc.RootElement.GetProperty("metadata").GetProperty("env").GetString());
        Assert.Equal("tier1", doc.RootElement.GetProperty("metadata").GetProperty("queue").GetString());
    }

    [Fact]
    public async Task Routing_same_event_twice_is_idempotent_in_outbox()
    {
        var store = new InMemoryHttpOutboxStore();
        var handler = new FakeHttpHandler();
        using var client = new HttpClient(handler);
        var outbox = new HttpRequestOutbox(store, new HttpClientRequestTransport(client));
        var router = new RecordAutomationRouter(outbox);

        var target = new WebhookTarget
        {
            Id = "crm",
            Name = "CRM",
            Url = "https://crm.example.test/webhook",
            Events = [RecordLifecycleEventType.MediaAdded],
        };

        var ev = new RecordLifecycleEvent
        {
            EventId = "evt-dedup",
            EventType = RecordLifecycleEventType.MediaAdded,
            FormId = "site-photo",
            RecordId = "rec-55",
            OccurredAtUtc = Now,
        };

        var first = await router.RouteAsync(ev, [target]);
        var second = await router.RouteAsync(ev, [target]);

        Assert.Equal(first.EnqueuedDeliveries[0].EntryId, second.EnqueuedDeliveries[0].EntryId);
        Assert.Single(await store.LoadAllAsync());
    }

    [Fact]
    public void AutomationSession_raises_new_lifecycle_triggers()
    {
        var form = new FormDefinition
        {
            FormId = "f1",
            Name = "Test",
            Sections =
            [
                new FormSection
                {
                    SectionId = "main",
                    Label = "Main",
                    Fields = [new FormField { FieldId = "status", Label = "Status", Type = FormFieldType.Text }],
                },
            ],
        };
        var session = FormSession.CreateForNewRecord(form, "r1");
        var rules = new[]
        {
            new AutomationRule { Name = "on-delete", Trigger = AutomationTrigger.RecordDelete },
            new AutomationRule { Name = "on-sync", Trigger = AutomationTrigger.RecordSync },
            new AutomationRule { Name = "on-status", Trigger = AutomationTrigger.StatusChanged },
            new AutomationRule { Name = "on-media", Trigger = AutomationTrigger.MediaAdded },
        };

        using var automation = AutomationSession.Attach(session, rules);

        Assert.Equal(["on-delete"], automation.RaiseRecordDelete().FiredRules);
        Assert.Equal(["on-sync"], automation.RaiseRecordSync().FiredRules);
        Assert.Equal(["on-status"], automation.RaiseStatusChanged().FiredRules);
        Assert.Equal(["on-media"], automation.RaiseMediaAdded().FiredRules);
    }
}
