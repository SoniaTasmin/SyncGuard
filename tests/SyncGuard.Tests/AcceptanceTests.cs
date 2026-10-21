using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SyncGuard.Core;
using SyncGuard.DestinationApi;

namespace SyncGuard.Tests;

/// <summary>The nine required acceptance scenarios, against real SQL Server.</summary>
[Collection(SqlCollection.Name)]
public sealed class AcceptanceTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;
    private TestSystem _sys = null!;

    public async Task InitializeAsync() => _sys = await TestSystem.StartAsync(sql);
    public async Task DisposeAsync() => await _sys.DisposeAsync();

    private DateTime Now => _sys.Time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task S0_order_and_outbox_event_commit_together_or_not_at_all()
    {
        await _sys.CreateOrderAsync("ORD-0001");

        var duplicate = await _sys.SourceClient.PostAsJsonAsync("/api/orders",
            new SourceApi.CreateOrderRequest("ORD-0001", "CUST-002", [new SourceApi.CreateOrderLine("ABC-1234", 1, 1m)]));
        var invalid = await _sys.SourceClient.PostAsJsonAsync("/api/orders",
            new SourceApi.CreateOrderRequest("ORD-0002", "", []));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await using var db = await _sys.SourceDb.CreateDbContextAsync();
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.OutboxEvents.CountAsync()); // the failed insert left no orphan event
        Assert.Equal(1, await db.Set<OrderLine>().CountAsync());
    }

    [Fact]
    public async Task S1_normal_order_synchronizes_successfully()
    {
        var eventId = await _sys.CreateOrderAsync("ORD-1001");

        await _sys.CreateDispatcher().DispatchDueAsync(None);

        var evt = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Delivered, evt.Status);
        Assert.Equal(1, evt.AttemptCount);
        Assert.Equal(201, evt.LastStatusCode);
        var row = Assert.Single(await _sys.DestinationRowsAsync());
        Assert.Equal("ORD-1001", row.OrderNumber);
        Assert.Equal(eventId.ToString(), row.IdempotencyKey);
        Assert.Equal(21.00m, row.TotalAmount);
        Assert.Equal(row.Id, evt.DestinationId);

        var report = await _sys.ReconcileAsync();
        Assert.True(report.IsConsistent);
        Assert.Equal(1, report.Counts.Matched);
    }

    [Fact]
    public async Task S2_http_503_eventually_recovers_without_losing_the_order()
    {
        await _sys.AddFaultAsync(new FaultRule(FaultKind.Http503, Count: 3));
        var eventId = await _sys.CreateOrderAsync("ORD-2001");
        var dispatcher = _sys.CreateDispatcher();

        await dispatcher.DispatchDueAsync(None);
        var first = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Pending, first.Status);
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal(503, first.LastStatusCode);
        // Attempt 1 backoff with base 1s and equal jitter lies in [0.5s, 1s].
        var wait = first.NextAttemptAtUtc - first.LastAttemptAtUtc!.Value;
        Assert.InRange(wait.TotalMilliseconds, 499, 1001);
        Assert.Empty(await _sys.DestinationRowsAsync());

        await dispatcher.DispatchDueAsync(None); // not due yet: nothing happens
        Assert.Equal(1, (await _sys.OutboxAsync(eventId)).AttemptCount);

        await _sys.SettleAsync(dispatcher);

        var evt = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Delivered, evt.Status);
        Assert.Equal(4, evt.AttemptCount);
        Assert.Single(await _sys.DestinationRowsAsync());
    }

    [Fact]
    public async Task S2b_unreachable_destination_and_timeouts_keep_the_event_pending_until_recovery()
    {
        var eventId = await _sys.CreateOrderAsync("ORD-2101");

        await _sys.CreateDispatcher(new RefusingHandler()).DispatchDueAsync(None);
        var down = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Pending, down.Status);
        Assert.StartsWith("Transport error", down.LastError);

        _sys.Options.RequestTimeout = TimeSpan.FromMilliseconds(300);
        await _sys.AddFaultAsync(new FaultRule(FaultKind.Timeout, DelayMs: 3_000));
        _sys.Time.Advance(TimeSpan.FromMinutes(1));
        await _sys.CreateDispatcher().DispatchDueAsync(None);
        var slow = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Pending, slow.Status);
        Assert.StartsWith("Timed out", slow.LastError);

        await _sys.SettleAsync(_sys.CreateDispatcher());
        Assert.Equal(OutboxStatus.Delivered, (await _sys.OutboxAsync(eventId)).Status);
        Assert.Equal(3, (await _sys.OutboxAsync(eventId)).AttemptCount);
        Assert.Single(await _sys.DestinationRowsAsync());
    }

    [Fact]
    public async Task S3_http_429_respects_retry_after()
    {
        await _sys.AddFaultAsync(new FaultRule(FaultKind.Http429, RetryAfterSeconds: 120));
        var eventId = await _sys.CreateOrderAsync("ORD-3001");
        var dispatcher = _sys.CreateDispatcher();

        await dispatcher.DispatchDueAsync(None);
        var limited = await _sys.OutboxAsync(eventId);
        Assert.Equal(429, limited.LastStatusCode);
        Assert.Equal(Now.AddSeconds(120), limited.NextAttemptAtUtc);

        _sys.Time.Advance(TimeSpan.FromSeconds(119));
        await dispatcher.DispatchDueAsync(None);
        Assert.Equal(1, (await _sys.OutboxAsync(eventId)).AttemptCount); // did not retry early

        _sys.Time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchDueAsync(None);
        var evt = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Delivered, evt.Status);
        Assert.Equal(2, evt.AttemptCount);
    }

    [Fact]
    public async Task S4_duplicate_requests_do_not_create_duplicate_destination_orders()
    {
        var key = Guid.NewGuid().ToString();
        var body = """{"eventId":"00000000-0000-0000-0000-000000000001","orderNumber":"ORD-4001","customerCode":"CUST-001","totalAmount":21.00,"createdAtUtc":"2026-01-05T09:00:00Z","lines":[{"sku":"ABC-1234","quantity":2,"unitPrice":10.50}]}""";

        var first = await PostRawAsync(key, body);
        var second = await PostRawAsync(key, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var r1 = await first.Content.ReadFromJsonAsync<Receipt>();
        var r2 = await second.Content.ReadFromJsonAsync<Receipt>();
        Assert.Equal(r1!.DestinationId, r2!.DestinationId);
        Assert.True(r2.Replayed);

        // Same key, different payload: refused rather than silently accepted.
        Assert.Equal(HttpStatusCode.Conflict, (await PostRawAsync(key, body.Replace("21.00", "22.00").Replace("10.50", "11.00"))).StatusCode);
        // Same order number under a different key: refused by the business unique key.
        Assert.Equal(HttpStatusCode.Conflict, (await PostRawAsync(Guid.NewGuid().ToString(), body)).StatusCode);

        // Concurrent identical requests race on the unique index; all succeed, one row exists.
        var raceKey = Guid.NewGuid().ToString();
        var raceBody = body.Replace("ORD-4001", "ORD-4002");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostRawAsync(raceKey, raceBody)));
        Assert.All(results, r => Assert.True(r.IsSuccessStatusCode, $"got {(int)r.StatusCode}"));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);

        Assert.Equal(2, (await _sys.DestinationRowsAsync()).Count);
    }

    [Fact]
    public async Task S4b_worker_redelivery_of_an_already_delivered_event_is_harmless()
    {
        var eventId = await _sys.CreateOrderAsync("ORD-4101");
        var dispatcher = _sys.CreateDispatcher();
        await dispatcher.DispatchDueAsync(None);
        var original = await _sys.OutboxAsync(eventId);

        // Simulate a crash after the destination committed but before the worker saved the outcome.
        await using (var db = await _sys.SourceDb.CreateDbContextAsync())
            await db.OutboxEvents.Where(e => e.Id == eventId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, OutboxStatus.Pending)
                .SetProperty(e => e.NextAttemptAtUtc, Now));

        await dispatcher.DispatchDueAsync(None);

        var evt = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Delivered, evt.Status);
        Assert.Equal(200, evt.LastStatusCode); // idempotent replay
        Assert.Equal(original.DestinationId, evt.DestinationId);
        Assert.Single(await _sys.DestinationRowsAsync());
    }

    [Fact]
    public async Task S5_lost_response_after_successful_persistence_is_not_duplicated_on_retry()
    {
        await _sys.AddFaultAsync(new FaultRule(FaultKind.LostResponse));
        var eventId = await _sys.CreateOrderAsync("ORD-5001");
        var dispatcher = _sys.CreateDispatcher();

        await dispatcher.DispatchDueAsync(None);
        var afterLoss = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Pending, afterLoss.Status);
        Assert.Null(afterLoss.LastStatusCode);
        Assert.StartsWith("Transport error", afterLoss.LastError);
        var row = Assert.Single(await _sys.DestinationRowsAsync()); // destination did commit

        await _sys.SettleAsync(dispatcher);

        var evt = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.Delivered, evt.Status);
        Assert.Equal(2, evt.AttemptCount);
        Assert.Equal(200, evt.LastStatusCode);
        Assert.Equal(row.Id, evt.DestinationId);
        Assert.Single(await _sys.DestinationRowsAsync());
    }

    [Fact]
    public async Task S6_restarting_the_worker_does_not_lose_pending_events()
    {
        _sys.Options.RequestTimeout = TimeSpan.FromSeconds(10);
        _sys.Options.BaseRetryDelay = TimeSpan.FromMilliseconds(100);
        _sys.Options.MaxRetryDelay = TimeSpan.FromMilliseconds(500);
        (await _sys.SourceClient.PostAsync("/api/orders/seed?count=200&seed=7", null)).EnsureSuccessStatusCode();
        // The first 8 requests hang, so worker #1 is stopped with all of its in-flight deliveries unfinished.
        await _sys.AddFaultAsync(new FaultRule(FaultKind.Timeout, Count: 8, DelayMs: 20_000));

        var real = TimeProvider.System;
        var worker1 = _sys.CreateWorker(_sys.CreateDispatcher(time: real), real);
        await worker1.StartAsync(None);
        await WaitUntilAsync(async () => await AttemptedAsync() >= 8, TimeSpan.FromSeconds(30));
        await worker1.StopAsync(None);

        Assert.Equal(0, await _sys.CountAsync(OutboxStatus.Delivered));
        Assert.Equal(200, await _sys.CountAsync(OutboxStatus.Pending));
        Assert.Equal(8, await AttemptedAsync());
        Assert.Empty(await _sys.DestinationRowsAsync());

        // A brand-new worker: no shared memory, only what is in SQL.
        var worker2 = _sys.CreateWorker(_sys.CreateDispatcher(time: real), real);
        await worker2.StartAsync(None);
        await WaitUntilAsync(async () => await _sys.CountAsync(OutboxStatus.Pending) == 0, TimeSpan.FromSeconds(60));
        await worker2.StopAsync(None);

        Assert.Equal(200, await _sys.CountAsync(OutboxStatus.Delivered));
        var rows = await _sys.DestinationRowsAsync();
        Assert.Equal(200, rows.Count);
        Assert.Equal(200, rows.Select(r => r.OrderNumber).Distinct().Count());
        await using var db = await _sys.SourceDb.CreateDbContextAsync();
        Assert.Equal(8, await db.OutboxEvents.CountAsync(e => e.AttemptCount == 2));

        async Task<int> AttemptedAsync()
        {
            await using var c = await _sys.SourceDb.CreateDbContextAsync();
            return await c.OutboxEvents.CountAsync(e => e.AttemptCount > 0);
        }
    }

    [Fact]
    public async Task S6b_lease_allows_one_dispatcher_and_is_taken_over_after_a_crash()
    {
        var a = _sys.CreateWorker(_sys.CreateDispatcher());
        var b = _sys.CreateWorker(_sys.CreateDispatcher());

        Assert.Contains("Degraded", await _sys.SourceClient.GetStringAsync("/health"));
        Assert.True(await a.TryAcquireLeaseAsync(None));
        Assert.False(await b.TryAcquireLeaseAsync(None));
        Assert.Equal("Healthy", await _sys.SourceClient.GetStringAsync("/health"));

        // "a" crashes without releasing; once its lease expires "b" takes over and "a" cannot reclaim it.
        _sys.Time.Advance(_sys.Options.LeaseDuration + TimeSpan.FromSeconds(1));
        Assert.True(await b.TryAcquireLeaseAsync(None));
        Assert.False(await a.TryAcquireLeaseAsync(None));
    }

    [Fact]
    public async Task S7_permanently_invalid_data_is_isolated_not_retried_forever()
    {
        var bad = await _sys.CreateOrderAsync("ORD-7001", sku: "not-a-sku");
        var good = await _sys.CreateOrderAsync("ORD-7002");
        var dispatcher = _sys.CreateDispatcher();

        await _sys.SettleAsync(dispatcher);

        var rejected = await _sys.OutboxAsync(bad);
        Assert.Equal(OutboxStatus.ManualReview, rejected.Status);
        Assert.Equal(1, rejected.AttemptCount);
        Assert.Equal(422, rejected.LastStatusCode);
        Assert.Contains("sku", rejected.LastError);
        Assert.Equal(OutboxStatus.Delivered, (await _sys.OutboxAsync(good)).Status);

        _sys.Time.Advance(TimeSpan.FromHours(1));
        await dispatcher.DispatchDueAsync(None);
        Assert.Equal(1, (await _sys.OutboxAsync(bad)).AttemptCount);
    }

    [Fact]
    public async Task S7b_retryable_failures_stop_at_max_attempts_and_go_to_manual_review()
    {
        _sys.Options.MaxAttempts = 3;
        await _sys.AddFaultAsync(new FaultRule(FaultKind.Http503, Count: 100));
        var eventId = await _sys.CreateOrderAsync("ORD-7101");

        await _sys.SettleAsync(_sys.CreateDispatcher());

        var evt = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.ManualReview, evt.Status);
        Assert.Equal(3, evt.AttemptCount);
        Assert.Equal(503, evt.LastStatusCode);
    }

    [Fact]
    public async Task S7c_event_whose_attempts_never_recorded_an_outcome_is_isolated_without_resending()
    {
        var eventId = await _sys.CreateOrderAsync("ORD-7201");
        // As if the worker had crashed mid-delivery MaxAttempts times (attempts are recorded before sending).
        await using (var db = await _sys.SourceDb.CreateDbContextAsync())
            await db.OutboxEvents.Where(e => e.Id == eventId).ExecuteUpdateAsync(s => s.SetProperty(e => e.AttemptCount, _sys.Options.MaxAttempts));

        await _sys.CreateDispatcher().DispatchDueAsync(None);

        var evt = await _sys.OutboxAsync(eventId);
        Assert.Equal(OutboxStatus.ManualReview, evt.Status);
        Assert.Equal(_sys.Options.MaxAttempts, evt.AttemptCount);
        Assert.Contains("without a recorded outcome", evt.LastError);
        Assert.Empty(await _sys.DestinationRowsAsync());
    }

    [Fact]
    public async Task S8_reconciliation_detects_missing_altered_unexpected_and_undelivered_orders()
    {
        for (var i = 1; i <= 5; i++) await _sys.CreateOrderAsync($"ORD-800{i}");
        await _sys.CreateOrderAsync("ORD-8006", sku: "bad sku");
        await _sys.SettleAsync(_sys.CreateDispatcher());

        await using (var db = _sys.DestinationDb())
        {
            await db.ReceivedOrders.Where(r => r.OrderNumber == "ORD-8001").ExecuteDeleteAsync();
            await db.ReceivedOrders.Where(r => r.OrderNumber == "ORD-8002").ExecuteUpdateAsync(s => s.SetProperty(r => r.TotalAmount, 999m));
            await db.ReceivedOrders.Where(r => r.OrderNumber == "ORD-8003").ExecuteUpdateAsync(s => s.SetProperty(r => r.CustomerCode, "CUST-999"));
            db.ReceivedOrders.Add(new ReceivedOrder
            {
                IdempotencyKey = "manual-insert", OrderNumber = "ORD-9999", CustomerCode = "CUST-001", TotalAmount = 1m,
                LinesJson = "[]", RequestHash = new string('0', 64), ReceivedAtUtc = Now,
            });
            await db.SaveChangesAsync();
        }
        await _sys.CreateOrderAsync("ORD-8007"); // just created, not yet dispatched: in flight

        var report = await _sys.ReconcileAsync(settleSeconds: 300);

        Assert.False(report.IsConsistent);
        Assert.False(report.IsSettled);
        Assert.Equal(new ReconciliationCountsView(7, 5, Matched: 2, InFlight: 1, Missing: 1, Mismatched: 2, Undelivered: 1, Unexpected: 1), View(report));
        Assert.Contains(report.Discrepancies, d => d is { Kind: "Missing", OrderNumber: "ORD-8001" });
        Assert.Contains(report.Discrepancies, d => d is { Kind: "Mismatched", OrderNumber: "ORD-8002" } && d.Detail.Contains("totalAmount"));
        Assert.Contains(report.Discrepancies, d => d is { Kind: "Mismatched", OrderNumber: "ORD-8003" } && d.Detail.Contains("customerCode"));
        Assert.Contains(report.Discrepancies, d => d is { Kind: "Undelivered", OrderNumber: "ORD-8006" });
        Assert.Contains(report.Discrepancies, d => d is { Kind: "Unexpected", OrderNumber: "ORD-9999" });

        // Past the settle window, a still-pending event is no longer "in flight" but a real gap.
        _sys.Time.Advance(TimeSpan.FromMinutes(10));
        var later = await _sys.ReconcileAsync(settleSeconds: 300);
        Assert.Equal(0, later.Counts.InFlight);
        Assert.Equal(2, later.Counts.Undelivered);
        Assert.Contains(later.Discrepancies, d => d is { Kind: "Undelivered", OrderNumber: "ORD-8007" } && d.Detail.Contains("settle window"));
    }

    [Fact]
    public async Task S9_one_thousand_orders_under_faults_settle_with_no_missing_or_duplicated_orders()
    {
        _sys.Options.RequestTimeout = TimeSpan.FromSeconds(1);
        var seeded = await (await _sys.SourceClient.PostAsync("/api/orders/seed?count=1000&seed=2026", null)).Content.ReadFromJsonAsync<SeedResult>();
        Assert.Equal(1000, seeded!.Created);

        await _sys.AddFaultAsync(new FaultRule(FaultKind.Http503, Count: 60, EveryNth: 7));
        await _sys.AddFaultAsync(new FaultRule(FaultKind.Http429, Count: 30, EveryNth: 11, RetryAfterSeconds: 5));
        await _sys.AddFaultAsync(new FaultRule(FaultKind.LostResponse, Count: 40, EveryNth: 13));
        await _sys.AddFaultAsync(new FaultRule(FaultKind.Http500, Count: 20, EveryNth: 17));
        await _sys.AddFaultAsync(new FaultRule(FaultKind.Timeout, Count: 5, EveryNth: 101, DelayMs: 2_000));

        await _sys.SettleAsync(_sys.CreateDispatcher(), step: TimeSpan.FromMinutes(3));

        Assert.Equal(1000, await _sys.CountAsync(OutboxStatus.Delivered));
        Assert.Equal(0, await _sys.CountAsync(OutboxStatus.ManualReview));
        var rows = await _sys.DestinationRowsAsync();
        Assert.Equal(1000, rows.Count);
        Assert.Equal(1000, rows.Select(r => r.OrderNumber).Distinct().Count());
        Assert.Equal(1000, rows.Select(r => r.IdempotencyKey).Distinct().Count());

        var report = await _sys.ReconcileAsync();
        Assert.True(report.IsConsistent, string.Join("; ", report.Discrepancies.Take(5)));
        Assert.True(report.IsSettled);
        Assert.Equal(1000, report.Counts.Matched);

        var status = await _sys.SourceClient.GetFromJsonAsync<StatusView>("/ops/status");
        Assert.Equal(1000, status!.Delivery.SuccessfulDeliveries);
        Assert.True(status.Delivery.RetryAttempts >= 150, $"retries: {status.Delivery.RetryAttempts}"); // every injected fault forced a retry
        Assert.NotNull(status.Delivery.LastSuccessfulSyncUtc);
        Assert.Equal(0, status.Outbox.Pending);
    }

    private Task<HttpResponseMessage> PostRawAsync(string key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", key);
        return _sys.DestinationClient.SendAsync(request);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time");
            await Task.Delay(100);
        }
    }

    private static ReconciliationCountsView View(SourceApi.ReconciliationReport r) => new(
        r.Counts.SourceOrders, r.Counts.DestinationOrders, r.Counts.Matched, r.Counts.InFlight,
        r.Counts.Missing, r.Counts.Mismatched, r.Counts.Undelivered, r.Counts.Unexpected);

    private sealed record ReconciliationCountsView(int Source, int Destination, int Matched, int InFlight, int Missing, int Mismatched, int Undelivered, int Unexpected);
    private sealed record Receipt(long DestinationId, bool Replayed);
    private sealed record SeedResult(int Created);
    private sealed record StatusView(OutboxView Outbox, DeliveryView Delivery);
    private sealed record OutboxView(int Pending);
    private sealed record DeliveryView(int SuccessfulDeliveries, int RetryAttempts, DateTime? LastSuccessfulSyncUtc);

    /// <summary>Behaves like a destination that is down (connection refused).</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused (simulated)");
    }
}
