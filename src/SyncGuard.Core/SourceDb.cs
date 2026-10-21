using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SyncGuard.Core;

public sealed class Order
{
    public int Id { get; set; }
    public required string OrderNumber { get; set; }
    public required string CustomerCode { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public required string Sku { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public enum OutboxStatus : byte { Pending = 0, Delivered = 1, ManualReview = 2 }

public sealed class OutboxEvent
{
    public const string OrderCreated = "OrderCreated";

    public Guid Id { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public required string EventType { get; set; }
    public required string Payload { get; set; }
    public OutboxStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public long? DestinationId { get; set; }
}

public sealed class WorkerLease
{
    public const string DispatcherName = "outbox-dispatcher";

    public required string Name { get; set; }
    public string? OwnerId { get; set; }
    public DateTime LeaseExpiresAtUtc { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
}

/// <summary>Schema is owned by db/source.sql; this model only maps it.</summary>
public sealed class SourceDbContext(DbContextOptions<SourceDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<WorkerLease> WorkerLeases => Set<WorkerLease>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcConverter>().HavePrecision(3);
        builder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Order>().HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId);
        model.Entity<OrderLine>().ToTable("OrderLines");
        model.Entity<OutboxEvent>().Property(e => e.AttemptCount).IsConcurrencyToken();
        model.Entity<WorkerLease>().HasKey(l => l.Name);
    }

    // SQL Server datetime2 has no kind; everything stored is UTC, so say so on read.
    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
