using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SyncGuard.Core;

namespace SyncGuard.SourceApi;

public sealed record CreateOrderRequest(string? OrderNumber, string? CustomerCode, List<CreateOrderLine>? Lines);
public sealed record CreateOrderLine(string? Sku, int Quantity, decimal UnitPrice);

public static class Orders
{
    /// <summary>
    /// Source-side rules only. The destination has stricter catalogue rules (SKU format) that the source does not know,
    /// which is how "permanently invalid" events arise in practice.
    /// </summary>
    public static Dictionary<string, string[]> Validate(CreateOrderRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.OrderNumber) || r.OrderNumber.Length > 50) e["orderNumber"] = ["Required, max 50 characters."];
        if (string.IsNullOrWhiteSpace(r.CustomerCode) || r.CustomerCode.Length > 20) e["customerCode"] = ["Required, max 20 characters."];
        if (r.Lines is not { Count: >= 1 and <= 100 }) { e["lines"] = ["1..100 lines required."]; return e; }
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            if (string.IsNullOrWhiteSpace(l.Sku) || l.Sku.Length > 30) e[$"lines[{i}].sku"] = ["Required, max 30 characters."];
            if (l.Quantity <= 0) e[$"lines[{i}].quantity"] = ["Must be positive."];
            if (l.UnitPrice <= 0 || decimal.Round(l.UnitPrice, 2) != l.UnitPrice) e[$"lines[{i}].unitPrice"] = ["Must be positive with at most 2 decimals."];
        }
        return e;
    }

    /// <summary>Adds the order and its outbox event to the context. Caller saves; one SaveChanges is one transaction.</summary>
    public static Order Stage(SourceDbContext db, CreateOrderRequest r, DateTime nowUtc)
    {
        var now = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc); // match datetime2(3)
        var order = new Order
        {
            OrderNumber = r.OrderNumber!.Trim(),
            CustomerCode = r.CustomerCode!.Trim(),
            CreatedAtUtc = now,
            Lines = r.Lines!.Select((l, i) => new OrderLine { LineNumber = i + 1, Sku = l.Sku!.Trim(), Quantity = l.Quantity, UnitPrice = l.UnitPrice }).ToList(),
        };
        order.TotalAmount = order.Lines.Sum(l => l.Quantity * l.UnitPrice);

        var eventId = Guid.NewGuid();
        db.Orders.Add(order);
        db.OutboxEvents.Add(new OutboxEvent
        {
            Id = eventId,
            Order = order,
            EventType = OutboxEvent.OrderCreated,
            Payload = JsonSerializer.Serialize(OrderMessage.From(eventId, order), OrderMessage.Json),
            Status = OutboxStatus.Pending,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return order;
    }

    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException is SqlException { Number: 2627 or 2601 };

    private static readonly string[] SkuPrefixes = ["ABC", "DEF", "GHJ", "KLM", "NPQ"];

    /// <summary>Same seed and count always produce the same orders. Existing order numbers are skipped, so re-running is safe.</summary>
    public static IEnumerable<CreateOrderRequest> Synthetic(int seed, int count)
    {
        var rng = new Random(seed);
        for (var i = 1; i <= count; i++)
        {
            var lines = Enumerable.Range(0, rng.Next(1, 5)).Select(_ => new CreateOrderLine(
                $"{SkuPrefixes[rng.Next(SkuPrefixes.Length)]}-{rng.Next(1000, 10000)}",
                rng.Next(1, 25),
                rng.Next(100, 50_000) / 100m)).ToList();
            yield return new CreateOrderRequest($"SEED{seed}-{i:D5}", $"CUST-{rng.Next(1, 51):D3}", lines);
        }
    }
}
