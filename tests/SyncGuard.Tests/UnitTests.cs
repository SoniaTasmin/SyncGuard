using System.Net.Http.Headers;
using SyncGuard.DestinationApi;
using SyncGuard.SourceApi;
using SyncGuard.Worker;

namespace SyncGuard.Tests;

public sealed class UnitTests
{
    [Theory]
    [InlineData(1, 500, 1_000)]
    [InlineData(2, 1_000, 2_000)]
    [InlineData(4, 4_000, 8_000)]
    [InlineData(20, 30_000, 60_000)] // capped at MaxRetryDelay
    public void Backoff_is_exponential_jittered_and_capped(int attempt, double minMs, double maxMs)
    {
        var low = RetryPolicy.Backoff(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 0.0);
        var high = RetryPolicy.Backoff(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 1.0);
        Assert.Equal(minMs, low.TotalMilliseconds);
        Assert.Equal(maxMs, high.TotalMilliseconds);
    }

    [Fact]
    public void Retry_after_supports_delta_and_http_date()
    {
        var now = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.RetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), now));
        Assert.Equal(TimeSpan.FromMinutes(2), RetryPolicy.RetryAfter(new RetryConditionHeaderValue(now.AddMinutes(2)), now));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.RetryAfter(new RetryConditionHeaderValue(now.AddMinutes(-2)), now));
        Assert.Null(RetryPolicy.RetryAfter(null, now));
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(409, false)]
    [InlineData(422, false)]
    public void Status_codes_are_classified(int status, bool retryable) => Assert.Equal(retryable, RetryPolicy.IsRetryable(status));

    [Fact]
    public void Fault_rules_fire_deterministically_on_every_nth_request()
    {
        var faults = new FaultInjector();
        faults.Add(new FaultRule(FaultKind.Http503, Count: 2, EveryNth: 3));
        var fired = Enumerable.Range(1, 10).Select(_ => faults.Next("ANY")?.Fault).ToList();
        Assert.Equal([null, null, FaultKind.Http503, null, null, FaultKind.Http503, null, null, null, null], fired);
    }

    [Fact]
    public void Targeted_fault_rules_only_apply_to_their_order()
    {
        var faults = new FaultInjector();
        faults.Add(new FaultRule(FaultKind.Http429, Count: 1, OrderNumber: "ORD-2"));
        Assert.Null(faults.Next("ORD-1"));
        Assert.Equal(FaultKind.Http429, faults.Next("ORD-2")?.Fault);
        Assert.Null(faults.Next("ORD-2")); // used up
    }

    [Theory]
    [InlineData(null, "NeverStarted")]
    [InlineData(0.0, "Healthy")]
    [InlineData(25.0, "Healthy")]
    [InlineData(25.1, "Stale")]
    public void Worker_is_stale_once_the_heartbeat_is_older_than_the_threshold(double? ageSeconds, string expected)
    {
        var now = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        var lease = new SyncGuard.Core.WorkerLease
        {
            Name = SyncGuard.Core.WorkerLease.DispatcherName,
            HeartbeatAtUtc = ageSeconds is { } a ? now.AddSeconds(-a) : null,
        };
        Assert.Equal(expected, Ops.WorkerState(lease, now, Ops.DefaultWorkerStaleAfter));
    }

    [Fact]
    public void Synthetic_orders_are_deterministic_per_seed()
    {
        var a = Orders.Synthetic(42, 50).Select(o => System.Text.Json.JsonSerializer.Serialize(o)).ToList();
        var b = Orders.Synthetic(42, 50).Select(o => System.Text.Json.JsonSerializer.Serialize(o)).ToList();
        var c = Orders.Synthetic(43, 50).Select(o => System.Text.Json.JsonSerializer.Serialize(o)).ToList();
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.All(Orders.Synthetic(42, 50), o => Assert.Empty(Orders.Validate(o)));
    }
}
