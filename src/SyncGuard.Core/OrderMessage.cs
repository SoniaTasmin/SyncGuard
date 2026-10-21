using System.Text.Json;

namespace SyncGuard.Core;

/// <summary>Wire contract sent to the destination. Serialized once into the outbox so retries send identical bytes.</summary>
public sealed record OrderMessage(
    Guid EventId,
    string OrderNumber,
    string CustomerCode,
    decimal TotalAmount,
    DateTime CreatedAtUtc,
    IReadOnlyList<OrderLineMessage> Lines)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static OrderMessage From(Guid eventId, Order order) => new(
        eventId, order.OrderNumber, order.CustomerCode, order.TotalAmount, order.CreatedAtUtc,
        order.Lines.OrderBy(l => l.LineNumber).Select(l => new OrderLineMessage(l.Sku, l.Quantity, l.UnitPrice)).ToList());
}

public sealed record OrderLineMessage(string Sku, int Quantity, decimal UnitPrice);
