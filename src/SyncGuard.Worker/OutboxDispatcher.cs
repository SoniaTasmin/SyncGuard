using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncGuard.Core;

namespace SyncGuard.Worker;

/// <summary>Delivers due outbox events at least once. Duplicates are made harmless by the destination's idempotency key.</summary>
public sealed class OutboxDispatcher(
    IDbContextFactory<SourceDbContext> dbFactory,
    HttpClient http,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<OutboxDispatcher> log)
{
    private readonly SyncOptions _o = options.Value;

    private enum Kind { Delivered, Retryable, Permanent }

    private sealed record Outcome(Kind Kind, int? StatusCode, string? Error = null, TimeSpan? RetryAfter = null, long? DestinationId = null)
    {
        /// <summary>Loggable description. Never includes response bodies, which may echo order data.</summary>
        public string SafeReason => StatusCode is { } s ? $"HTTP {s}" : Error ?? "unknown";
    }

    /// <summary>Processes one batch of due events. Returns how many were picked up.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var now = Now();
        List<Guid> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            due = await db.OutboxEvents
                .Where(e => e.Status == OutboxStatus.Pending && e.NextAttemptAtUtc <= now)
                .OrderBy(e => e.NextAttemptAtUtc)
                .Select(e => e.Id)
                .Take(_o.BatchSize)
                .ToListAsync(ct);
        }

        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = _o.MaxParallelism, CancellationToken = ct }, DeliverAsync);
        return due.Count;
    }

    private async ValueTask DeliverAsync(Guid eventId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var evt = await db.OutboxEvents.FirstOrDefaultAsync(e => e.Id == eventId && e.Status == OutboxStatus.Pending, ct);
        if (evt is null) return;

        using var _ = log.BeginScope(new Dictionary<string, object> { ["OutboxEventId"] = evt.Id, ["OrderId"] = evt.OrderId });

        var startedAt = Now();
        if (evt.AttemptCount >= _o.MaxAttempts)
        {
            // Earlier attempts never recorded an outcome (crash or unexpected error). Stop rather than loop forever.
            evt.Status = OutboxStatus.ManualReview;
            evt.LastError = Truncate($"{evt.AttemptCount} attempts without a recorded outcome. Last: {evt.LastError}", 1000);
            await db.SaveChangesAsync(ct);
            log.LogError("Attempts exhausted without a recorded outcome; moved to manual review");
            return;
        }

        // Claim and record the attempt before sending. If the process dies mid-request the attempt still counts
        // and the event only becomes due again after the backoff. AttemptCount is a concurrency token,
        // so if two dispatchers ever overlap only one of them can claim a given attempt.
        evt.AttemptCount++;
        evt.LastAttemptAtUtc = startedAt;
        evt.NextAttemptAtUtc = startedAt + Backoff(evt.AttemptCount);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            log.LogWarning("Event was claimed by another dispatcher; skipping");
            return;
        }

        Outcome outcome;
        using var timeout = new CancellationTokenSource(_o.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/orders")
            {
                Content = new StringContent(evt.Payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", evt.Id.ToString());
            using var response = await http.SendAsync(request, linked.Token);
            outcome = await ClassifyAsync(response, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            outcome = new Outcome(Kind.Retryable, null, $"Timed out after {_o.RequestTimeout.TotalSeconds:0.#}s");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Connection refused/reset/aborted - possibly after the destination committed. Retrying is safe (idempotent).
            outcome = new Outcome(Kind.Retryable, null, $"Transport error: {ex.Message}");
        }
        // Cancellation of ct (shutdown / lease boundary) propagates: the event stays Pending with the attempt recorded.

        Apply(evt, outcome);
        try
        {
            // The outcome is already known; persist it even if shutdown has started.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            log.LogWarning("Outcome not saved: a newer attempt has claimed this event and will record its own");
        }
    }

    private async Task<Outcome> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            long? destinationId = null;
            try { destinationId = (await response.Content.ReadFromJsonAsync<Receipt>(OrderMessage.Json, ct))?.DestinationId; }
            catch (JsonException) { log.LogWarning("Destination accepted the order but returned an unreadable receipt"); }
            return new Outcome(Kind.Delivered, status, DestinationId: destinationId);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var error = $"HTTP {status}: {Truncate(body, 500)}";
        return RetryPolicy.IsRetryable(status)
            ? new Outcome(Kind.Retryable, status, error, RetryPolicy.RetryAfter(response.Headers.RetryAfter, time.GetUtcNow()))
            : new Outcome(Kind.Permanent, status, error);
    }

    private void Apply(OutboxEvent evt, Outcome outcome)
    {
        var now = Now();
        evt.LastStatusCode = outcome.StatusCode;
        evt.LastError = outcome.Error is null ? null : Truncate(outcome.Error, 1000); // column is NVARCHAR(1000)

        switch (outcome.Kind)
        {
            case Kind.Delivered:
                evt.Status = OutboxStatus.Delivered;
                evt.DeliveredAtUtc = now;
                evt.DestinationId = outcome.DestinationId;
                log.LogInformation("Delivered after {AttemptCount} attempt(s) as destination {DestinationId}", evt.AttemptCount, outcome.DestinationId);
                break;

            case Kind.Retryable when evt.AttemptCount >= _o.MaxAttempts:
                evt.Status = OutboxStatus.ManualReview;
                log.LogError("Retries exhausted after {AttemptCount} attempts (last failure: {Failure}); moved to manual review",
                    evt.AttemptCount, outcome.SafeReason);
                break;

            case Kind.Retryable:
                // Honor server guidance when given; otherwise use our own jittered backoff.
                var delay = outcome.RetryAfter ?? Backoff(evt.AttemptCount);
                evt.NextAttemptAtUtc = now + delay;
                log.LogWarning("Attempt {AttemptCount} failed ({Failure}); retrying in {RetryDelayMs} ms, honoring Retry-After: {HonoredRetryAfter}",
                    evt.AttemptCount, outcome.SafeReason, (long)delay.TotalMilliseconds, outcome.RetryAfter is not null);
                break;

            case Kind.Permanent:
                evt.Status = OutboxStatus.ManualReview;
                log.LogError("Permanent rejection ({Failure}) on attempt {AttemptCount}; moved to manual review",
                    outcome.SafeReason, evt.AttemptCount);
                break;
        }
    }

    private TimeSpan Backoff(int attempt) => RetryPolicy.Backoff(attempt, _o.BaseRetryDelay, _o.MaxRetryDelay, Random.Shared.NextDouble());

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record Receipt(long DestinationId);
}
