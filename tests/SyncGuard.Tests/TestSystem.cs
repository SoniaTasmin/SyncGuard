using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SyncGuard.Core;
using SyncGuard.DestinationApi;
using SyncGuard.SourceApi;
using SyncGuard.Worker;
using Testcontainers.MsSql;

namespace SyncGuard.Tests;

/// <summary>One SQL Server container for the whole run; every test gets its own pair of databases.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync(string script)
    {
        var name = $"sg_{Guid.NewGuid():N}";
        await using (var master = new SqlConnection(_container.GetConnectionString()))
        {
            await master.OpenAsync();
            await new SqlCommand($"CREATE DATABASE [{name}]", master).ExecuteNonQueryAsync();
        }

        var cs = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", script));
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => !string.IsNullOrWhiteSpace(b)))
            await new SqlCommand(batch, conn).ExecuteNonQueryAsync();
        return cs;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}

/// <summary>Source API + mock destination (in-memory test servers) over real SQL Server, with a controllable clock.</summary>
public sealed class TestSystem : IAsyncDisposable
{
    /// <summary>Shared by the source API and the dispatcher.</summary>
    public required FakeTimeProvider Time { get; init; }
    public SyncOptions Options { get; } = new()
    {
        BatchSize = 50,
        MaxParallelism = 8,
        MaxAttempts = 8,
        RequestTimeout = TimeSpan.FromSeconds(2),
        BaseRetryDelay = TimeSpan.FromSeconds(1),
        MaxRetryDelay = TimeSpan.FromSeconds(30),
        PollInterval = TimeSpan.FromMilliseconds(50),
        LeaseDuration = TimeSpan.FromSeconds(30),
    };

    public required string SourceConnectionString { get; init; }
    public required string DestinationConnectionString { get; init; }
    public required WebApplicationFactory<DestinationDbContext> Destination { get; init; }
    public required WebApplicationFactory<Reconciler> Source { get; init; }
    public required HttpClient SourceClient { get; init; }
    public required HttpClient DestinationClient { get; init; }
    public required IDbContextFactory<SourceDbContext> SourceDb { get; init; }

    public static async Task<TestSystem> StartAsync(SqlServerFixture sql)
    {
        var sourceCs = await sql.CreateDatabaseAsync("source.sql");
        var destinationCs = await sql.CreateDatabaseAsync("destination.sql");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));

        var destination = new WebApplicationFactory<DestinationDbContext>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Destination", destinationCs);
            b.UseSetting("FaultInjection:Enabled", "true");
        });
        var source = new WebApplicationFactory<Reconciler>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Source", sourceCs);
            b.UseSetting("Destination:BaseUrl", "http://localhost/");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(time);
                s.AddHttpClient<Reconciler>().ConfigurePrimaryHttpMessageHandler(() => destination.Server.CreateHandler());
            });
        });

        return new TestSystem
        {
            Time = time,
            SourceConnectionString = sourceCs,
            DestinationConnectionString = destinationCs,
            Destination = destination,
            Source = source,
            SourceClient = source.CreateClient(),
            DestinationClient = destination.CreateClient(),
            SourceDb = new PooledDbContextFactory<SourceDbContext>(new DbContextOptionsBuilder<SourceDbContext>().UseSqlServer(sourceCs).Options),
        };
    }

    public OutboxDispatcher CreateDispatcher(HttpMessageHandler? handler = null, TimeProvider? time = null) => new(
        SourceDb,
        new HttpClient(handler ?? Destination.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") },
        Microsoft.Extensions.Options.Options.Create(Options),
        time ?? Time,
        NullLogger<OutboxDispatcher>.Instance);

    public SyncWorker CreateWorker(OutboxDispatcher dispatcher, TimeProvider? time = null) => new(
        dispatcher, SourceDb, Microsoft.Extensions.Options.Options.Create(Options), time ?? Time, NullLogger<SyncWorker>.Instance);

    /// <summary>Runs dispatch cycles, advancing the fake clock between them, until nothing is pending.</summary>
    public async Task<int> SettleAsync(OutboxDispatcher dispatcher, TimeSpan? step = null, int maxCycles = 500)
    {
        for (var cycle = 1; cycle <= maxCycles; cycle++)
        {
            await dispatcher.DispatchDueAsync(CancellationToken.None);
            if (await CountAsync(OutboxStatus.Pending) == 0) return cycle;
            Time.Advance(step ?? TimeSpan.FromMinutes(1));
        }
        throw new Xunit.Sdk.XunitException($"Outbox did not settle within {maxCycles} cycles");
    }

    public async Task<Guid> CreateOrderAsync(string orderNumber, string sku = "ABC-1234", int quantity = 2, decimal unitPrice = 10.50m)
    {
        var response = await SourceClient.PostAsJsonAsync("/api/orders",
            new CreateOrderRequest(orderNumber, "CUST-001", [new CreateOrderLine(sku, quantity, unitPrice)]));
        if (!response.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"Create order failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<CreatedOrder>();
        return body!.EventId;
    }

    public async Task AddFaultAsync(FaultRule rule) =>
        (await DestinationClient.PostAsJsonAsync("/fault-injection/rules", rule, FaultJson)).EnsureSuccessStatusCode();

    public async Task<OutboxEvent> OutboxAsync(Guid eventId)
    {
        await using var db = await SourceDb.CreateDbContextAsync();
        return await db.OutboxEvents.AsNoTracking().SingleAsync(e => e.Id == eventId);
    }

    public async Task<int> CountAsync(OutboxStatus status)
    {
        await using var db = await SourceDb.CreateDbContextAsync();
        return await db.OutboxEvents.CountAsync(e => e.Status == status);
    }

    public DestinationDbContext DestinationDb() =>
        new(new DbContextOptionsBuilder<DestinationDbContext>().UseSqlServer(DestinationConnectionString).Options);

    public async Task<List<ReceivedOrder>> DestinationRowsAsync()
    {
        await using var db = DestinationDb();
        return await db.ReceivedOrders.AsNoTracking().ToListAsync();
    }

    public async Task<ReconciliationReport> ReconcileAsync(int settleSeconds = 300) =>
        (await SourceClient.GetFromJsonAsync<ReconciliationReport>($"/ops/reconciliation?settleSeconds={settleSeconds}", OrderMessage.Json))!;

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await Destination.DisposeAsync();
    }

    public static readonly System.Text.Json.JsonSerializerOptions FaultJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private sealed record CreatedOrder(string OrderNumber, decimal TotalAmount, Guid EventId);
}
