var builder = DistributedApplication.CreateBuilder(args);

// Tier 2/3 backing store for local dev. SQL Server is also supported by
// ServerHost (config-selected, see docs/00-design-document.md §4.3) but is
// not orchestrated here to keep the local dev topology to one instance;
// switch EventStore:Provider + the resource below to run against SQL
// Server instead.
var eventStoreDb = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("EventStore");

// NATS leaf-node topology (ADR-0002; see its 2026-07-23 Amendment for how
// this was validated). Generic containers, not the Aspire.Hosting.NATS
// package, because a real leaf-node relationship needs custom config files
// (leafnodes { ... } blocks) that package doesn't expose knobs for.
var natsHub = builder.AddContainer("nats-hub", "nats", "2-alpine")
    .WithBindMount("nats-config/hub.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
    .WithArgs("-c", "/etc/nats/nats-server.conf")
    .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
    .WithEndpoint(targetPort: 7422, name: "leafnode", scheme: "tcp");

// The leaf container's static config dials "nats-leaf://nats-hub:7422" —
// Aspire puts same-run container resources on a shared network reachable
// by resource name, so this works without further wiring.
var natsLeaf = builder.AddContainer("nats-leaf", "nats", "2-alpine")
    .WithBindMount("nats-config/leaf.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
    .WithArgs("-c", "/etc/nats/nats-server.conf")
    .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
    .WaitFor(natsHub);

builder.AddProject<Projects.SyncMesh_ServerHost>("serverhost")
    .WithReference(eventStoreDb)
    .WaitFor(eventStoreDb)
    .WithEnvironment("EventStore__Provider", "Postgres")
    .WithEnvironment(context =>
    {
        var endpoint = natsHub.GetEndpoint("client");
        context.EnvironmentVariables["ServerHost__Nats__Url"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}");
    })
    .WaitFor(natsHub);

// Tier 1: SQLite-backed, file-local — no container/backing resource needed
// for its own durability, but it does need its embedded leaf node.
builder.AddProject<Projects.SyncMesh_Daemon>("daemon")
    .WithEnvironment(context =>
    {
        var endpoint = natsLeaf.GetEndpoint("client");
        context.EnvironmentVariables["Daemon__Nats__Url"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}");
    })
    .WaitFor(natsLeaf);

// Mesh-wide passive-monitoring dashboard (backend) — subscribes to
// monitor.> on the hub side, same vantage point ApplyResponder/
// ServerMonitorPublisher already use. See docs/00-design-document.md §4.5.
builder.AddProject<Projects.SyncMesh_MeshMonitor_Api>("mesh-monitor-api")
    .WithEnvironment(context =>
    {
        var endpoint = natsHub.GetEndpoint("client");
        context.EnvironmentVariables["MeshMonitor__NatsUrl"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}");
    })
    .WaitFor(natsHub);

builder.Build().Run();
