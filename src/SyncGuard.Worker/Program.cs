using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncGuard.Core;
using SyncGuard.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection("Sync"))
    .Validate(o => o.IsValid(), "Invalid Sync options (LeaseDuration must exceed 2x RequestTimeout).")
    .ValidateOnStart();
builder.Services.AddDbContextFactory<SourceDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Source")
        ?? throw new InvalidOperationException("ConnectionStrings:Source is not configured.")));
builder.Services.AddSingleton(TimeProvider.System);
// Long-lived client with pooled-connection recycling (the recommended pattern for a singleton HttpClient).
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<SyncOptions>>().Value;
    return new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        BaseAddress = o.DestinationBaseUrl,
        Timeout = Timeout.InfiniteTimeSpan, // per-request timeout is enforced by OutboxDispatcher
    };
});
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddHostedService<SyncWorker>();

builder.Build().Run();
