using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SyncGuard.Core;

namespace SyncGuard.SourceApi;

public static class Ops
{
    /// <summary>Operational summary. Everything is derived from SQL, so it survives worker restarts.</summary>
    public static async Task<object> StatusAsync(SourceDbContext db, TimeProvider time, TimeSpan workerStaleAfter, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var byStatus = await db.OutboxEvents
            .GroupBy(e => e.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                Attempts = g.Sum(e => e.AttemptCount),
                Retries = g.Sum(e => e.AttemptCount > 1 ? e.AttemptCount - 1 : 0),
                NeededRetry = g.Count(e => e.AttemptCount > 1),
            })
            .ToListAsync(ct);
        int Count(OutboxStatus s) => byStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        var pending = db.OutboxEvents.Where(e => e.Status == OutboxStatus.Pending);
        var lastDelivered = await db.OutboxEvents.MaxAsync(e => e.DeliveredAtUtc, ct);
        var oldestPending = await pending.MinAsync(e => (DateTime?)e.CreatedAtUtc, ct);
        var lease = await db.WorkerLeases.AsNoTracking().SingleOrDefaultAsync(l => l.Name == WorkerLease.DispatcherName, ct);

        return new
        {
            generatedAtUtc = now,
            worker = new
            {
                status = WorkerState(lease, now, workerStaleAfter),
                lastHeartbeatUtc = lease?.HeartbeatAtUtc,
                leaseOwner = lease?.OwnerId,
                leaseExpiresAtUtc = lease?.LeaseExpiresAtUtc,
            },
            outbox = new
            {
                pending = Count(OutboxStatus.Pending),
                pendingDueNow = await pending.CountAsync(e => e.NextAttemptAtUtc <= now, ct),
                pendingWaitingForRetry = await pending.CountAsync(e => e.AttemptCount > 0, ct),
                delivered = Count(OutboxStatus.Delivered),
                manualReview = Count(OutboxStatus.ManualReview),
                oldestPendingCreatedAtUtc = oldestPending,
            },
            delivery = new
            {
                totalAttempts = byStatus.Sum(x => x.Attempts),
                retryAttempts = byStatus.Sum(x => x.Retries),
                eventsThatNeededRetry = byStatus.Sum(x => x.NeededRetry),
                successfulDeliveries = Count(OutboxStatus.Delivered),
                lastSuccessfulSyncUtc = lastDelivered,
            },
        };
    }

    public static string WorkerState(WorkerLease? lease, DateTime now, TimeSpan staleAfter) => lease?.HeartbeatAtUtc switch
    {
        null => "NeverStarted",
        var hb when hb >= now - staleAfter => "Healthy",
        _ => "Stale",
    };
}

/// <summary>Reports Degraded (still HTTP 200) when the worker heartbeat is missing or stale.</summary>
public sealed class WorkerHeartbeatCheck(SourceDbContext db, TimeProvider time, IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var lease = await db.WorkerLeases.AsNoTracking().SingleOrDefaultAsync(l => l.Name == WorkerLease.DispatcherName, ct);
        var state = Ops.WorkerState(lease, time.GetUtcNow().UtcDateTime, config.GetValue("Ops:WorkerStaleAfter", TimeSpan.FromSeconds(45)));
        return state == "Healthy" ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded($"Worker heartbeat: {state}");
    }
}
