using Microsoft.EntityFrameworkCore;
using SyncGuard.Core;
using SyncGuard.SourceApi;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<SourceDbContext>(o =>
    o.UseSqlServer(config.GetConnectionString("Source")
        ?? throw new InvalidOperationException("ConnectionStrings:Source is not configured.")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<Reconciler>(c =>
{
    c.BaseAddress = new Uri(config["Destination:BaseUrl"] ?? "http://localhost:5002/");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SourceDbContext>("database")
    .AddCheck<WorkerHeartbeatCheck>("worker");

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapPost("/api/orders", async (CreateOrderRequest request, SourceDbContext db, TimeProvider time, ILogger<Program> log, CancellationToken ct) =>
{
    var errors = Orders.Validate(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    var order = Orders.Stage(db, request, time.GetUtcNow().UtcDateTime);
    try
    {
        await db.SaveChangesAsync(ct); // order, lines and outbox event commit atomically
    }
    catch (DbUpdateException ex) when (Orders.IsUniqueViolation(ex))
    {
        return Results.Problem(statusCode: 409, title: $"Order {order.OrderNumber} already exists.");
    }

    var eventId = db.OutboxEvents.Local.Single().Id;
    log.LogInformation("Created order {OrderNumber} with outbox event {EventId}", order.OrderNumber, eventId);
    return Results.Created($"/api/orders/{order.OrderNumber}", new { order.OrderNumber, order.TotalAmount, eventId });
});

app.MapPost("/api/orders/seed", async (SourceDbContext db, TimeProvider time, ILogger<Program> log, CancellationToken ct, int count = 100, int seed = 42) =>
{
    if (count is < 1 or > 10_000) return Results.Problem(statusCode: 400, title: "count must be 1..10000");

    var requests = Orders.Synthetic(seed, count).ToList();
    var numbers = requests.Select(r => r.OrderNumber!).ToList();
    var existing = (await db.Orders.Where(o => numbers.Contains(o.OrderNumber)).Select(o => o.OrderNumber).ToListAsync(ct)).ToHashSet();

    var created = 0;
    foreach (var chunk in requests.Where(r => !existing.Contains(r.OrderNumber!)).Chunk(200))
    {
        foreach (var r in chunk) Orders.Stage(db, r, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct); // each chunk: orders + their outbox events in one transaction
        db.ChangeTracker.Clear();
        created += chunk.Length;
    }

    log.LogInformation("Seeded {Created} orders (seed {Seed}, {Skipped} already existed)", created, seed, existing.Count);
    return Results.Ok(new { seed, requested = count, created, skippedExisting = existing.Count });
});

app.MapGet("/api/orders/{orderNumber}", async (string orderNumber, SourceDbContext db, CancellationToken ct) =>
{
    var result = await db.OutboxEvents.AsNoTracking()
        .Where(e => e.Order!.OrderNumber == orderNumber)
        .Select(e => new
        {
            e.Order!.OrderNumber, e.Order.CustomerCode, e.Order.TotalAmount, e.Order.CreatedAtUtc,
            outbox = new
            {
                eventId = e.Id, status = e.Status.ToString(), e.AttemptCount, e.NextAttemptAtUtc, e.LastAttemptAtUtc,
                e.DeliveredAtUtc, e.LastStatusCode, e.LastError, e.DestinationId,
            },
        })
        .SingleOrDefaultAsync(ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapGet("/ops/status", (SourceDbContext db, TimeProvider time, CancellationToken ct) =>
    Ops.StatusAsync(db, time, config.GetValue("Ops:WorkerStaleAfter", Ops.DefaultWorkerStaleAfter), ct));

app.MapGet("/ops/reconciliation", async (Reconciler reconciler, CancellationToken ct, int? settleSeconds) =>
{
    var window = settleSeconds is { } s ? TimeSpan.FromSeconds(Math.Max(0, s)) : config.GetValue("Reconciliation:SettleWindow", TimeSpan.FromMinutes(5));
    return await reconciler.RunAsync(window, ct);
});

app.Run();
