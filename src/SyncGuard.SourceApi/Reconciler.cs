using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SyncGuard.Core;

namespace SyncGuard.SourceApi;

public sealed record Discrepancy(string Kind, string OrderNumber, Guid? EventId, string Detail);

public sealed record ReconciliationCounts(
    int SourceOrders, int DestinationOrders, int Matched, int InFlight,
    int Missing, int Mismatched, int Undelivered, int Unexpected, int Duplicated);

public sealed record ReconciliationReport(
    DateTime GeneratedAtUtc, DateTime WatermarkUtc, double SettleWindowSeconds,
    bool IsConsistent, bool IsSettled, ReconciliationCounts Counts, IReadOnlyList<Discrepancy> Discrepancies);

/// <summary>
/// Compares source orders with what the destination says it has booked.
/// Quiescence rule: a Pending event younger than the settle window is "in flight", not a discrepancy.
/// </summary>
public sealed class Reconciler(SourceDbContext db, HttpClient destination, TimeProvider time)
{
    private const int MaxListed = 200;

    private sealed record DestinationOrder(long Id, string IdempotencyKey, string OrderNumber, string CustomerCode, decimal TotalAmount, List<OrderLineMessage> Lines);
    private sealed record DestinationPage(List<DestinationOrder> Items, long? NextAfterId);

    // ponytail: loads both sides into memory; fine to ~10^5 orders, window by CreatedAtUtc ranges beyond that.
    public async Task<ReconciliationReport> RunAsync(TimeSpan settleWindow, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var watermark = now - settleWindow;

        // Source first, then destination. An event Delivered in this snapshot was committed at the destination
        // before we read it, so "Delivered but absent" is a real loss, not a race.
        var orders = await db.Orders.AsNoTracking().Include(o => o.Lines).ToListAsync(ct);
        var events = await db.OutboxEvents.AsNoTracking()
            .Select(e => new { e.OrderId, e.Id, e.Status, e.LastError, e.AttemptCount })
            .ToDictionaryAsync(e => e.OrderId, ct);

        var remote = new List<DestinationOrder>();
        long? after = 0;
        while (after is not null)
        {
            var page = await destination.GetFromJsonAsync<DestinationPage>($"api/orders?afterId={after}&take=1000", OrderMessage.Json, ct)
                ?? throw new InvalidOperationException("Destination returned an empty reconciliation page.");
            remote.AddRange(page.Items);
            after = page.NextAfterId;
        }
        var byNumber = remote.GroupBy(r => r.OrderNumber).ToDictionary(g => g.Key, g => g.ToList());

        var found = new List<Discrepancy>();
        int matched = 0, inFlight = 0, missing = 0, mismatched = 0, undelivered = 0, duplicated = 0;

        foreach (var order in orders)
        {
            events.TryGetValue(order.Id, out var evt);
            if (byNumber.TryGetValue(order.OrderNumber, out var copies))
            {
                if (copies.Count > 1)
                {
                    duplicated++;
                    found.Add(new("Duplicated", order.OrderNumber, evt?.Id, $"{copies.Count} destination records"));
                }
                var diff = Differences(order, copies[0]);
                if (diff.Count > 0)
                {
                    mismatched++;
                    found.Add(new("Mismatched", order.OrderNumber, evt?.Id, "Differs in: " + string.Join(", ", diff)));
                }
                else matched++;
                continue;
            }

            switch (evt?.Status)
            {
                case OutboxStatus.Delivered:
                    missing++;
                    found.Add(new("Missing", order.OrderNumber, evt.Id, "Acknowledged as delivered but absent at destination"));
                    break;
                case OutboxStatus.ManualReview:
                    undelivered++;
                    found.Add(new("Undelivered", order.OrderNumber, evt.Id, $"Manual review after {evt.AttemptCount} attempt(s): {evt.LastError}"));
                    break;
                case OutboxStatus.Pending when order.CreatedAtUtc > watermark:
                    inFlight++;
                    break;
                default:
                    undelivered++;
                    found.Add(new("Undelivered", order.OrderNumber, evt?.Id,
                        evt is null ? "No outbox event" : $"Still pending beyond the settle window after {evt.AttemptCount} attempt(s): {evt.LastError}"));
                    break;
            }
        }

        // Destination records with no source order. Re-check the database: the order may have been created after our snapshot.
        var sourceNumbers = orders.Select(o => o.OrderNumber).ToHashSet();
        var candidates = byNumber.Keys.Where(n => !sourceNumbers.Contains(n)).ToList();
        var lateArrivals = candidates.Count == 0 ? [] :
            await db.Orders.AsNoTracking().Where(o => candidates.Contains(o.OrderNumber)).Select(o => o.OrderNumber).ToListAsync(ct);
        var unexpected = candidates.Except(lateArrivals).ToList();
        found.AddRange(unexpected.Select(n => new Discrepancy("Unexpected", n, null, "Exists at destination but not in source")));

        var counts = new ReconciliationCounts(orders.Count, remote.Count, matched, inFlight, missing, mismatched, undelivered, unexpected.Count, duplicated);
        return new ReconciliationReport(now, watermark, settleWindow.TotalSeconds,
            IsConsistent: found.Count == 0,
            IsSettled: inFlight == 0,
            counts, found.Take(MaxListed).ToList());
    }

    private static List<string> Differences(Order source, DestinationOrder dest)
    {
        var diff = new List<string>();
        if (source.CustomerCode != dest.CustomerCode) diff.Add("customerCode");
        if (source.TotalAmount != dest.TotalAmount) diff.Add("totalAmount");
        var lines = source.Lines.OrderBy(l => l.LineNumber).Select(l => new OrderLineMessage(l.Sku, l.Quantity, l.UnitPrice));
        if (!lines.SequenceEqual(dest.Lines)) diff.Add("lines");
        return diff;
    }
}
