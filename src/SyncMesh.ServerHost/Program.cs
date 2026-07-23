using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.EventStore;
using SyncMesh.ServerHost.Nats;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Tier 2/3: system of record. On-prem/WAN/cloud selection is configuration,
// not code — see docs/00-design-document.md §4.3. The provider itself is
// also config-selected (PostgreSQL or SQL Server), per ADR-0001.
var provider = builder.Configuration["EventStore:Provider"]
    ?? throw new InvalidOperationException("Missing configuration value 'EventStore:Provider' (expected 'Postgres' or 'SqlServer').");
var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? throw new InvalidOperationException("Missing configuration value 'ConnectionStrings:EventStore'.");

switch (provider)
{
    case "Postgres":
        builder.Services.AddPostgresEventStore(connectionString);
        break;
    case "SqlServer":
        builder.Services.AddSqlServerEventStore(connectionString);
        break;
    default:
        throw new InvalidOperationException($"Unsupported EventStore:Provider '{provider}'. Expected 'Postgres' or 'SqlServer'.");
}

builder.Services
    .AddOptions<ServerNatsOptions>()
    .Bind(builder.Configuration.GetSection(ServerNatsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<ServerMeshOptions>()
    .Bind(builder.Configuration.GetSection(ServerMeshOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// One NATS connection per server process, to its own local cluster — never
// a shared connection into a peer's cluster (see docs/adr/0002-nats-leaf-
// nodes-for-transport.md's 2026-07-23 (Phase 3) Amendment: MeshForwarder
// dials each peer directly instead).
builder.Services.AddSingleton(sp =>
    new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<ServerNatsOptions>>().Value.Url }));
builder.Services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));

// Registration order matters — the generic host starts hosted services in
// order: the MESH_OUTBOUND stream/consumers must exist before
// ApplyResponder starts relaying into it or MeshForwarder starts pulling
// from it.
builder.Services.AddHostedService<ServerMeshSetup>();
builder.Services.AddHostedService<ApplyResponder>();
builder.Services.AddHostedService<MeshForwarder>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
}

host.Run();
