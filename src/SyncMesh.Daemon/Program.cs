using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.Daemon;
using SyncMesh.Daemon.Ipc;
using SyncMesh.Daemon.Nats;
using SyncMesh.EventStore;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<DaemonOptions>()
    .Bind(builder.Configuration.GetSection(DaemonOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<DaemonNatsOptions>()
    .Bind(builder.Configuration.GetSection(DaemonNatsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<DaemonMonitorOptions>()
    .Bind(builder.Configuration.GetSection(DaemonMonitorOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Tier 1: local, durable-only-while-recording buffer. Always SQLite — see
// docs/adr/0001-event-store-on-ef-core.md.
var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? "Data Source=daemon-events.db";
builder.Services.AddSqliteEventStore(connectionString);

// One HlcGenerator per daemon process — its counter must be monotonic
// across every event this process produces. See docs/06-data-model.md §3.
builder.Services.AddSingleton<HlcGenerator>();

// One NATS connection per daemon process, to its embedded local leaf node
// (never the nearest server directly) — see docs/00-design-document.md §4.2.
builder.Services.AddSingleton(sp =>
    new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<DaemonNatsOptions>>().Value.Url }));
builder.Services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));

builder.Services.AddScoped<LocalEventWriter>();
builder.Services.AddScoped<LocalEventReader>();

// Registration order matters: the generic host starts hosted services in
// order, so the stream/consumer must exist before the IPC listener accepts
// writes or the forwarder starts pulling.
builder.Services.AddHostedService<DaemonJetStreamSetup>();
builder.Services.AddHostedService<LocalIpcListener>();

// Registered as its own singleton (not just AddHostedService<T>, which
// only makes T resolvable as IHostedService) so MonitorPublisher can read
// its live ForwardedCount — the same running instance the host starts.
builder.Services.AddSingleton<EventForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventForwarder>());

// Passive monitoring (Tier X) — architecturally separate from the event-
// sync path above: its own subject namespace, no JetStream, no shared
// failure domain. See docs/00-design-document.md §4.5.
builder.Services.AddHostedService<MonitorPublisher>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
}

host.Run();
