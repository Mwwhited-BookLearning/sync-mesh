using SyncMesh.EventStore;
using SyncMesh.ServerHost;

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

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
