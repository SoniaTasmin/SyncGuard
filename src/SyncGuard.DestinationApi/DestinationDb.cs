using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SyncGuard.DestinationApi;

public sealed class ReceivedOrder
{
    public long Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string OrderNumber { get; set; }
    public required string CustomerCode { get; set; }
    public decimal TotalAmount { get; set; }
    public required string LinesJson { get; set; }
    public required string RequestHash { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}

/// <summary>Schema is owned by db/destination.sql.</summary>
public sealed class DestinationDbContext(DbContextOptions<DestinationDbContext> options) : DbContext(options)
{
    public DbSet<ReceivedOrder> ReceivedOrders => Set<ReceivedOrder>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcConverter>().HavePrecision(3);
        builder.Properties<decimal>().HavePrecision(18, 2);
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
