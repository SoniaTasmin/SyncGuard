using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncGuard.Core;

namespace SyncGuard.Worker;

/// <summary>
/// Poll loop. Holds a time-bounded lease row so only one dispatcher is active at a time,
/// and refreshes the heartbeat that /ops/status reports as worker health.
/// </summary>
public sealed class SyncWorker(
    OutboxDispatcher dispatcher,
    IDbContextFactory<SourceDbContext> dbFactory,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<SyncWorker> log) : BackgroundService
{
    private readonly SyncOptions _o = options.Value;
    public string OwnerId { get; } = $"{Environment.MachineName[..Math.Min(50, Environment.MachineName.Length)]}/{Environment.ProcessId}/{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Sync worker {OwnerId} started", OwnerId);
        var standingBy = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                if (await TryAcquireLeaseAsync(stoppingToken))
                {
                    if (standingBy) log.LogInformation("Lease acquired by {OwnerId}", OwnerId);
                    standingBy = false;

                    // A cycle must finish well inside the lease it was started under.
                    using var cycleCts = new CancellationTokenSource(_o.LeaseDuration * 2 / 3, time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cycleCts.Token);
                    try
                    {
                        processed = await dispatcher.DispatchDueAsync(linked.Token);
                    }
                    catch (OperationCanceledException) when (cycleCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        log.LogWarning("Dispatch cycle reached the lease time budget; in-flight events will be retried");
                    }
                }
                else if (!standingBy)
                {
                    standingBy = true;
                    log.LogWarning("Lease is held by another worker; standing by");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Typically the database is unreachable. Nothing is lost: state lives in SQL. Log and try again.
                log.LogError(ex, "Dispatch cycle failed; retrying after {PollInterval}", _o.PollInterval);
            }

            if (processed == 0)
            {
                try { await Task.Delay(_o.PollInterval, time, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        await ReleaseLeaseAsync();
        log.LogInformation("Sync worker {OwnerId} stopped", OwnerId);
    }

    /// <summary>Atomic compare-and-set on the lease row: succeeds if we hold it or it has expired.</summary>
    internal async Task<bool> TryAcquireLeaseAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkerLeases
            .Where(l => l.Name == WorkerLease.DispatcherName && (l.OwnerId == OwnerId || l.LeaseExpiresAtUtc < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.OwnerId, OwnerId)
                .SetProperty(l => l.LeaseExpiresAtUtc, now + _o.LeaseDuration)
                .SetProperty(l => l.HeartbeatAtUtc, now), ct);
        return rows == 1;
    }

    /// <summary>On graceful shutdown hand the lease back so a restarted worker resumes immediately.</summary>
    private async Task ReleaseLeaseAsync()
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.WorkerLeases
                .Where(l => l.Name == WorkerLease.DispatcherName && l.OwnerId == OwnerId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.LeaseExpiresAtUtc, time.GetUtcNow().UtcDateTime));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not release lease; another worker can take over once it expires");
        }
    }
}
