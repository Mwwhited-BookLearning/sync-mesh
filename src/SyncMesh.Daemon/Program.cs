using SyncMesh.Daemon;
using SyncMesh.EventStore;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Tier 1: local, durable-only-while-recording buffer. Always SQLite — see
// docs/adr/0001-event-store-on-ef-core.md.
var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? "Data Source=daemon-events.db";
builder.Services.AddSqliteEventStore(connectionString);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
