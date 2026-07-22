var builder = DistributedApplication.CreateBuilder(args);

// Tier 2/3 backing store for local dev. SQL Server is also supported by
// ServerHost (config-selected, see docs/00-design-document.md §4.3) but is
// not orchestrated here to keep the local dev topology to one instance;
// switch EventStore:Provider + the resource below to run against SQL
// Server instead.
var eventStoreDb = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("EventStore");

// NATS leaf/gateway wiring (Tier 1/2/3 transport, ADR-0002) lands in
// Phase 2 — not orchestrated yet since nothing consumes it.

builder.AddProject<Projects.SyncMesh_ServerHost>("serverhost")
    .WithReference(eventStoreDb)
    .WaitFor(eventStoreDb)
    .WithEnvironment("EventStore__Provider", "Postgres");

// Tier 1: SQLite-backed, file-local — no container/backing resource needed.
builder.AddProject<Projects.SyncMesh_Daemon>("daemon");

builder.Build().Run();
