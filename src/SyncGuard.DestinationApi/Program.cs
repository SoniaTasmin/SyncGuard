using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SyncGuard.DestinationApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<DestinationDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Destination")
        ?? throw new InvalidOperationException("ConnectionStrings:Destination is not configured.")));
builder.Services.AddHealthChecks().AddDbContextCheck<DestinationDbContext>("database");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);

var faultInjectionEnabled = builder.Configuration.GetValue<bool>("FaultInjection:Enabled");
builder.Services.AddSingleton<FaultInjector>();

var app = builder.Build();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var skuPattern = new Regex(@"^[A-Z]{3}-\d{4}$", RegexOptions.Compiled);

app.MapHealthChecks("/health");

app.MapPost("/api/orders", async (HttpContext ctx, DestinationDbContext db, FaultInjector faults, TimeProvider time, ILogger<Program> log) =>
{
    var key = ctx.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required (max 100 characters).");

    using var _ = log.BeginScope(new Dictionary<string, object> { ["IdempotencyKey"] = key }); // = source outbox event id

    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync(ctx.RequestAborted);

    IncomingOrder? order;
    try { order = JsonSerializer.Deserialize<IncomingOrder>(body, json); }
    catch (JsonException) { return Results.Problem(statusCode: 400, title: "Body is not valid JSON for an order."); }
    if (order is null) return Results.Problem(statusCode: 400, title: "Body is required.");

    var fault = faultInjectionEnabled ? faults.Next(order.OrderNumber) : null;
    if (fault is not null) log.LogWarning("Fault injection: applying {Fault}", fault.Fault);
    switch (fault?.Fault)
    {
        case FaultKind.Http500: return Results.Problem(statusCode: 500, title: "Injected fault");
        case FaultKind.Http503:
        case FaultKind.Http429:
            if (fault.RetryAfterSeconds is { } s) ctx.Response.Headers.RetryAfter = s.ToString();
            return Results.Problem(statusCode: fault.Fault == FaultKind.Http429 ? 429 : 503, title: "Injected fault");
        case FaultKind.Timeout:
            try { await Task.Delay(fault.DelayMs, ctx.RequestAborted); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return Results.Empty; }
            return Results.Problem(statusCode: 504, title: "Injected timeout");
    }

    var errors = Validate(order);
    if (errors.Count > 0)
    {
        log.LogWarning("Rejected order {OrderNumber}: {ErrorFields}", order.OrderNumber, string.Join(",", errors.Keys));
        return Results.ValidationProblem(errors, statusCode: 422);
    }

    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    var existing = await db.ReceivedOrders.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == key);
    if (existing is not null) return Replay(existing, hash, log);

    var row = new ReceivedOrder
    {
        IdempotencyKey = key,
        OrderNumber = order.OrderNumber!,
        CustomerCode = order.CustomerCode!,
        TotalAmount = order.TotalAmount,
        LinesJson = JsonSerializer.Serialize(order.Lines, json),
        RequestHash = hash,
        ReceivedAtUtc = time.GetUtcNow().UtcDateTime,
    };
    db.ReceivedOrders.Add(row);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
    {
        // A concurrent duplicate won the race, or the order number was already booked under another key.
        existing = await db.ReceivedOrders.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == key);
        if (existing is not null) return Replay(existing, hash, log);
        log.LogWarning("Order {OrderNumber} already booked under a different idempotency key", row.OrderNumber);
        return Results.Problem(statusCode: 409, title: "Order number already booked under a different idempotency key.");
    }

    log.LogInformation("Accepted order {OrderNumber} as {DestinationId}", row.OrderNumber, row.Id);

    if (fault?.Fault == FaultKind.LostResponse)
    {
        log.LogWarning("Fault injection: order {OrderNumber} persisted, dropping the response", row.OrderNumber);
        ctx.Abort();
        return Results.Empty;
    }

    return Results.Created($"/api/orders/{row.Id}", Receipt(row, replayed: false));

    Dictionary<string, string[]> Validate(IncomingOrder o)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(o.OrderNumber) || o.OrderNumber.Length > 50) e["orderNumber"] = ["Required, max 50 characters."];
        if (string.IsNullOrWhiteSpace(o.CustomerCode) || o.CustomerCode.Length > 20) e["customerCode"] = ["Required, max 20 characters."];
        if (o.Lines is not { Count: >= 1 and <= 100 }) { e["lines"] = ["1..100 lines required."]; return e; }
        for (var i = 0; i < o.Lines.Count; i++)
        {
            var l = o.Lines[i];
            if (l.Sku is null || !skuPattern.IsMatch(l.Sku)) e[$"lines[{i}].sku"] = ["Unknown SKU format; expected AAA-0000."];
            if (l.Quantity is < 1 or > 10_000) e[$"lines[{i}].quantity"] = ["Must be 1..10000."];
            if (l.UnitPrice is <= 0 or > 1_000_000) e[$"lines[{i}].unitPrice"] = ["Must be > 0 and <= 1000000."];
        }
        if (e.Count == 0 && o.Lines.Sum(l => l.Quantity * l.UnitPrice) != o.TotalAmount) e["totalAmount"] = ["Does not equal the sum of lines."];
        return e;
    }
});

// Reconciliation feed: keyset-paged list of everything this system has booked.
app.MapGet("/api/orders", async (DestinationDbContext db, long afterId = 0, int take = 500) =>
{
    take = Math.Clamp(take, 1, 1000);
    var rows = await db.ReceivedOrders.AsNoTracking().Where(r => r.Id > afterId).OrderBy(r => r.Id).Take(take).ToListAsync();
    var items = rows.Select(r => new
    {
        r.Id, r.IdempotencyKey, r.OrderNumber, r.CustomerCode, r.TotalAmount, r.ReceivedAtUtc,
        Lines = JsonSerializer.Deserialize<List<IncomingLine>>(r.LinesJson, json),
    });
    return Results.Ok(new { items, nextAfterId = rows.Count == take ? rows[^1].Id : (long?)null });
});

if (faultInjectionEnabled)
{
    app.Logger.LogWarning("FAULT INJECTION IS ENABLED. Demo/test use only; never enable in a real environment.");
    var faults = app.MapGroup("/fault-injection/rules");
    faults.MapGet("", (FaultInjector f) => f.Snapshot());
    faults.MapPost("", (FaultRule rule, FaultInjector f) =>
    {
        if (rule.Validate() is { } error) return Results.Problem(statusCode: 400, title: error);
        f.Add(rule);
        return Results.Ok(f.Snapshot());
    });
    faults.MapDelete("", (FaultInjector f) => { f.Clear(); return Results.NoContent(); });
}

app.Run();

static IResult Replay(ReceivedOrder existing, string hash, ILogger log)
{
    if (existing.RequestHash != hash)
    {
        log.LogWarning("Idempotency key reused with a different payload for order {OrderNumber}", existing.OrderNumber);
        return Results.Problem(statusCode: 409, title: "Idempotency key already used with a different payload.");
    }
    log.LogInformation("Duplicate request for order {OrderNumber}; returning original result {DestinationId}", existing.OrderNumber, existing.Id);
    return Results.Ok(Receipt(existing, replayed: true));
}

static object Receipt(ReceivedOrder r, bool replayed) =>
    new { destinationId = r.Id, idempotencyKey = r.IdempotencyKey, orderNumber = r.OrderNumber, receivedAtUtc = r.ReceivedAtUtc, replayed };

record IncomingOrder(Guid EventId, string? OrderNumber, string? CustomerCode, decimal TotalAmount, DateTime CreatedAtUtc, List<IncomingLine>? Lines);
record IncomingLine(string? Sku, int Quantity, decimal UnitPrice);
