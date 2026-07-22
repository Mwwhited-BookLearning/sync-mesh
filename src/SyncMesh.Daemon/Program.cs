using SyncMesh.Contracts;
using SyncMesh.Daemon;
using SyncMesh.Daemon.Ipc;
using SyncMesh.EventStore;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<DaemonOptions>()
    .Bind(builder.Configuration.GetSection(DaemonOptions.SectionName))
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

builder.Services.AddScoped<LocalEventWriter>();
builder.Services.AddScoped<LocalEventReader>();
builder.Services.AddHostedService<LocalIpcListener>();

var host = builder.Build();
host.Run();
