using Honua.Collect.Core.Automation;
using Honua.Collect.Core.Automation.Http;

namespace Honua.Collect.Core.Tests.Automation;

/// <summary>
/// Tests for the #44 durable HTTP outbox: a request queued while offline stays
/// pending and replays once connectivity returns, with idempotency-key de-dup,
/// exponential-backoff retry, a terminal status, and a permanent-failure path —
/// all against a fake transport and a fake clock, no live network.
/// </summary>
public class HttpRequestOutboxTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A scriptable transport: each call pops the next queued result; records what it sent.</summary>
    private sealed class FakeTransport : IHttpRequestTransport
    {
        private readonly Queue<HttpSendResult> _results;
        public List<HttpOutboxRequest> Sent { get; } = [];

        public FakeTransport(params HttpSendResult[] results)
            => _results = new Queue<HttpSendResult>(results);

        public bool Online { get; set; } = true;

        public Task<HttpSendResult> SendAsync(HttpOutboxRequest request, CancellationToken ct = default)
        {
            Sent.Add(request);
            if (!Online)
            {
                return Task.FromResult(HttpSendResult.Offline());
            }

            var result = _results.Count > 0 ? _results.Dequeue() : HttpSendResult.Ok();
            return Task.FromResult(result);
        }
    }

    private static HttpOutboxRequest Request(string key = "key-1")
        => new() { Url = "https://example.test/hook", Body = "{}", IdempotencyKey = key };

    [Fact]
    public async Task Offline_enqueue_stays_pending_then_replays_when_connectivity_returns()
    {
        var clock = new FakeClock(Start);
        var store = new InMemoryHttpOutboxStore();
        var transport = new FakeTransport { Online = false };
        var outbox = new HttpRequestOutbox(store, transport, clock);

        await outbox.EnqueueAsync(Request(), "rule-A");

        // Offline drain: the request is attempted but cannot be delivered; it stays pending.
        var offline = await outbox.DrainAsync();
        Assert.Equal(0, offline.Sent);
        Assert.Equal(1, offline.Retried);

        var afterOffline = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(HttpOutboxStatus.Pending, afterOffline.Status);

        // Connectivity returns; advance past the backoff and drain again — it sends.
        transport.Online = true;
        clock.Advance(TimeSpan.FromMinutes(5));
        var online = await outbox.DrainAsync();

        Assert.Equal(1, online.Sent);
        var delivered = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(HttpOutboxStatus.Sent, delivered.Status);
        Assert.Equal("https://example.test/hook", Assert.Single(transport.Sent.TakeLast(1)).Url);
    }

    [Fact]
    public async Task Online_enqueue_sends_on_first_drain()
    {
        var store = new InMemoryHttpOutboxStore();
        var transport = new FakeTransport(HttpSendResult.Ok(201));
        var outbox = new HttpRequestOutbox(store, transport, new FakeClock(Start));

        await outbox.EnqueueAsync(Request());
        var result = await outbox.DrainAsync();

        Assert.Equal(1, result.Sent);
        var entry = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(HttpOutboxStatus.Sent, entry.Status);
        Assert.Equal(201, entry.LastStatusCode);
        Assert.Equal(1, entry.Attempts);
    }

    [Fact]
    public async Task Same_idempotency_key_is_not_queued_twice()
    {
        var store = new InMemoryHttpOutboxStore();
        var outbox = new HttpRequestOutbox(store, new FakeTransport(), new FakeClock(Start));

        var first = await outbox.EnqueueAsync(Request("dup"), "rule-A");
        var second = await outbox.EnqueueAsync(Request("dup"), "rule-A");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Transient_failure_retries_with_growing_backoff_then_succeeds()
    {
        var clock = new FakeClock(Start);
        var store = new InMemoryHttpOutboxStore();
        // 503 (retryable), 503 (retryable), then 200.
        var transport = new FakeTransport(
            new HttpSendResult(503), new HttpSendResult(503), HttpSendResult.Ok());
        var policy = new HttpOutboxRetryPolicy(MaxAttempts: 5,
            BaseDelay: TimeSpan.FromSeconds(30), MaxDelay: TimeSpan.FromHours(1));
        var outbox = new HttpRequestOutbox(store, transport, clock, policy);

        await outbox.EnqueueAsync(Request());

        // Attempt 1 -> 503, reschedule ~30s out; not due immediately.
        Assert.Equal(1, (await outbox.DrainAsync()).Retried);
        Assert.Equal(0, (await outbox.DrainAsync()).Total); // still backing off
        var afterFirst = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal(503, afterFirst.LastStatusCode);

        // After 30s, attempt 2 -> 503, reschedule ~60s out (backoff doubled).
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, (await outbox.DrainAsync()).Retried);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, (await outbox.DrainAsync()).Total); // 60s backoff not yet elapsed

        // After the full 60s, attempt 3 -> 200.
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, (await outbox.DrainAsync()).Sent);

        var entry = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(HttpOutboxStatus.Sent, entry.Status);
        Assert.Equal(3, entry.Attempts);
        Assert.Equal(3, transport.Sent.Count); // replayed, same request each time
        Assert.All(transport.Sent, r => Assert.Equal("key-1", r.IdempotencyKey));
    }

    [Fact]
    public async Task Retry_budget_exhaustion_marks_permanently_failed()
    {
        var clock = new FakeClock(Start);
        var store = new InMemoryHttpOutboxStore();
        var transport = new FakeTransport(
            new HttpSendResult(500), new HttpSendResult(500));
        var policy = new HttpOutboxRetryPolicy(MaxAttempts: 2,
            BaseDelay: TimeSpan.FromSeconds(10), MaxDelay: TimeSpan.FromMinutes(1));
        var outbox = new HttpRequestOutbox(store, transport, clock, policy);

        await outbox.EnqueueAsync(Request());

        Assert.Equal(1, (await outbox.DrainAsync()).Retried); // attempt 1
        clock.Advance(TimeSpan.FromSeconds(11));
        var second = await outbox.DrainAsync(); // attempt 2 = last -> failed

        Assert.Equal(1, second.Failed);
        var entry = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(HttpOutboxStatus.Failed, entry.Status);
        Assert.Equal(2, entry.Attempts);
    }

    [Fact]
    public async Task Non_retryable_client_error_fails_immediately()
    {
        var store = new InMemoryHttpOutboxStore();
        var transport = new FakeTransport(new HttpSendResult(400, "Bad Request"));
        var outbox = new HttpRequestOutbox(store, transport, new FakeClock(Start));

        await outbox.EnqueueAsync(Request());
        var result = await outbox.DrainAsync();

        Assert.Equal(1, result.Failed);
        var entry = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(HttpOutboxStatus.Failed, entry.Status);
        Assert.Equal(400, entry.LastStatusCode);
        Assert.Equal(1, entry.Attempts); // no retry on a 4xx
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task Throwing_transport_is_treated_as_transient_not_lost()
    {
        var store = new InMemoryHttpOutboxStore();
        var outbox = new HttpRequestOutbox(store, new ThrowingTransport(), new FakeClock(Start));

        await outbox.EnqueueAsync(Request());
        var result = await outbox.DrainAsync();

        Assert.Equal(1, result.Retried);
        var entry = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(HttpOutboxStatus.Pending, entry.Status);
        Assert.Contains("boom", entry.LastError);
    }

    private sealed class ThrowingTransport : IHttpRequestTransport
    {
        public Task<HttpSendResult> SendAsync(HttpOutboxRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task EnqueueFrom_folds_automation_result_http_requests_into_the_outbox()
    {
        var store = new InMemoryHttpOutboxStore();
        var transport = new FakeTransport(HttpSendResult.Ok());
        var outbox = new HttpRequestOutbox(store, transport, new FakeClock(Start));

        // A rule that queues an HTTP request via the runtime (no network in the run).
        var rule = new AutomationRule
        {
            Name = "queue-webhook",
            Trigger = AutomationTrigger.RecordSave,
            Actions = [HttpRequestAction.Post("https://example.test/hook", "{\"flag\":true}", "rec-42")],
        };
        var result = new AutomationRuntime().Run(
            [rule],
            new AutomationEvent(AutomationTrigger.RecordSave),
            new Dictionary<string, object?>());

        var queued = Assert.Single(result.HttpRequests);
        Assert.Equal("rec-42", queued.Request.IdempotencyKey);

        var enqueued = await outbox.EnqueueFromAsync(result);
        Assert.Equal(1, enqueued);

        var sent = await outbox.DrainAsync();
        Assert.Equal(1, sent.Sent);
    }
}
