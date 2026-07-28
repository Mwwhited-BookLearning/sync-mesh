var builder = DistributedApplication.CreateBuilder(args);

// Two sites, full multi-server mesh — each site is a complete Tier
// 0-3 stack (daemon + its own nearest server), and the two servers are
// peered directly (ADR-0002's Phase 3 Amendment: point-to-point, not
// native NATS gateway/supercluster). This is what makes mesh convergence
// something you can actually watch happen via the Order Book demo
// (docs/06-data-model.md's Order Book Example Domain section) rather
// than only something proven in tests.
//
// Server tier uses SQLite here, not Postgres/SQL Server (see ADR-0001's
// Amendment): a containerized Postgres in this sandbox hit a compounding
// set of Aspire/DCP issues (data-volume/password mismatch across runs, a
// WaitFor deadlock on the database resource, a dual-stack "localhost"
// NATS connection failure) that are about Aspire/Docker orchestration,
// not this project's own event-sourcing model — not worth the ongoing
// friction for a dev/POC topology. Each site's ServerHost gets its own
// file, both under one absolute directory so orderbook-api (a different
// project, different working directory) can point at site A's file by
// the same absolute path.
var dataDir = Path.Combine(builder.AppHostDirectory, ".data");
Directory.CreateDirectory(dataDir);
var eventStoreAPath = Path.Combine(dataDir, "site-a-events.db");
var eventStoreBPath = Path.Combine(dataDir, "site-b-events.db");

// NATS leaf-node topology per site (ADR-0002). Generic containers, not
// the Aspire.Hosting.NATS package, because a real leaf-node relationship
// needs custom config files (leafnodes { ... } blocks) that package
// doesn't expose knobs for. hub.conf is identical for both sites (no
// site-specific values in it) and is reused as-is; leaf-a.conf/
// leaf-b.conf differ only in which hub they dial.
var natsHubA = builder.AddContainer("nats-hub-a", "nats", "2-alpine")
    .WithBindMount("nats-config/hub.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
    .WithArgs("-c", "/etc/nats/nats-server.conf")
    .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
    .WithEndpoint(targetPort: 7422, name: "leafnode", scheme: "tcp");

var natsLeafA = builder.AddContainer("nats-leaf-a", "nats", "2-alpine")
    .WithBindMount("nats-config/leaf-a.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
    .WithArgs("-c", "/etc/nats/nats-server.conf")
    .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
    .WaitFor(natsHubA);

var natsHubB = builder.AddContainer("nats-hub-b", "nats", "2-alpine")
    .WithBindMount("nats-config/hub.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
    .WithArgs("-c", "/etc/nats/nats-server.conf")
    .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
    .WithEndpoint(targetPort: 7422, name: "leafnode", scheme: "tcp");

var natsLeafB = builder.AddContainer("nats-leaf-b", "nats", "2-alpine")
    .WithBindMount("nats-config/leaf-b.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
    .WithArgs("-c", "/etc/nats/nats-server.conf")
    .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
    .WaitFor(natsHubB);

// Site A's server. Tunnel ports left at their smart defaults
// (7777/7778/7779) — site B's server/daemon are given explicit
// non-default ports below since both sites' processes share this one
// machine's port space (unlike the NATS containers, the tunnel's plain
// TCP listeners aren't Aspire-managed endpoints, so they need distinct
// literal ports, not dynamic allocation).
var serverHostA = builder.AddProject<Projects.SyncMesh_ServerHost>("serverhost-a")
    .WithEnvironment("ConnectionStrings__EventStore", $"Data Source={eventStoreAPath}")
    .WithEnvironment("EventStore__Provider", "Sqlite")
    .WithEnvironment("ServerHost__Monitor__SiteId", "site-a")
    .WithEnvironment("ServerHost__Monitor__InstanceId", "server-a")
    .WithEnvironment(context =>
    {
        var endpoint = natsHubA.GetEndpoint("client");
        context.EnvironmentVariables["ServerHost__Nats__Url"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.IPV4Host)}:{endpoint.Property(EndpointProperty.Port)}");
    })
    .WithEnvironment(context =>
    {
        var peerEndpoint = natsHubB.GetEndpoint("client");
        context.EnvironmentVariables["ServerHost__Mesh__Peers__0__SiteId"] = "site-b";
        context.EnvironmentVariables["ServerHost__Mesh__Peers__0__Url"] = ReferenceExpression.Create($"nats://{peerEndpoint.Property(EndpointProperty.IPV4Host)}:{peerEndpoint.Property(EndpointProperty.Port)}");
    })
    .WaitFor(natsHubA);

// Site B's server — same shape as site A, peered back at it, on distinct
// tunnel ports.
var serverHostB = builder.AddProject<Projects.SyncMesh_ServerHost>("serverhost-b")
    .WithEnvironment("ConnectionStrings__EventStore", $"Data Source={eventStoreBPath}")
    .WithEnvironment("EventStore__Provider", "Sqlite")
    .WithEnvironment("ServerHost__Monitor__SiteId", "site-b")
    .WithEnvironment("ServerHost__Monitor__InstanceId", "server-b")
    .WithEnvironment("ServerHost__Tunnel__AgentListenPort", "7788")
    .WithEnvironment("ServerHost__Tunnel__ClientListenPort", "7789")
    .WithEnvironment(context =>
    {
        var endpoint = natsHubB.GetEndpoint("client");
        context.EnvironmentVariables["ServerHost__Nats__Url"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.IPV4Host)}:{endpoint.Property(EndpointProperty.Port)}");
    })
    .WithEnvironment(context =>
    {
        var peerEndpoint = natsHubA.GetEndpoint("client");
        context.EnvironmentVariables["ServerHost__Mesh__Peers__0__SiteId"] = "site-a";
        context.EnvironmentVariables["ServerHost__Mesh__Peers__0__Url"] = ReferenceExpression.Create($"nats://{peerEndpoint.Property(EndpointProperty.IPV4Host)}:{peerEndpoint.Property(EndpointProperty.Port)}");
    })
    .WaitFor(natsHubB);

// Tier 1: SQLite-backed, file-local per daemon. Distinct IPC pipe names
// (both daemon processes run on this one machine) and, for site B, a
// distinct tunnel direct-listen port + relay URL matching serverhost-b's
// tunnel ports above.
builder.AddProject<Projects.SyncMesh_Daemon>("daemon-a")
    .WithEnvironment("Daemon__SiteId", "site-a")
    .WithEnvironment("Daemon__InstanceId", "daemon-a")
    .WithEnvironment("Daemon__IpcPipeName", "syncmesh-daemon-a")
    .WithEnvironment(context =>
    {
        var endpoint = natsLeafA.GetEndpoint("client");
        context.EnvironmentVariables["Daemon__Nats__Url"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.IPV4Host)}:{endpoint.Property(EndpointProperty.Port)}");
    })
    .WaitFor(natsLeafA);

builder.AddProject<Projects.SyncMesh_Daemon>("daemon-b")
    .WithEnvironment("Daemon__SiteId", "site-b")
    .WithEnvironment("Daemon__InstanceId", "daemon-b")
    .WithEnvironment("Daemon__IpcPipeName", "syncmesh-daemon-b")
    .WithEnvironment("Daemon__Tunnel__DirectListenPort", "7787")
    .WithEnvironment("Daemon__Tunnel__RelayUrl", "localhost:7788")
    .WithEnvironment(context =>
    {
        var endpoint = natsLeafB.GetEndpoint("client");
        context.EnvironmentVariables["Daemon__Nats__Url"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.IPV4Host)}:{endpoint.Property(EndpointProperty.Port)}");
    })
    .WaitFor(natsLeafB);

// Mesh-wide passive-monitoring dashboard (backend) — subscribes to
// monitor.> on BOTH sites' hubs: the two sites' NATS clusters are
// completely separate (only the ServerHost<->ServerHost mesh peering
// bridges them, and only for event replication, not general pub/sub), so
// covering the whole mesh means one subscription per site's hub, not one
// shared connection. See MeshMonitorApiOptions.NatsUrls' doc comment.
// A stable dev-only signing key: Aspire generates one on first run and
// persists it in this AppHost project's user-secrets, so it's the same
// value across restarts (retrieve it with `dotnet user-secrets list
// --project src/SyncMesh.AppHost` to mint a test JWT signed with it) —
// unlike the Postgres password issue this AppHost previously hit, there's
// no separate persisted container state for this value to fall out of
// sync with. See docs/adr/0009-ticket-based-signalr-auth.md — this
// dashboard still doesn't issue tokens itself, this key only lets a
// pre-minted test JWT validate against this dev topology.
var meshMonitorSigningKey = builder.AddParameter(
    "mesh-monitor-signing-key",
    new GenerateParameterDefault { MinLength = 44, Special = false },
    secret: true);

builder.AddProject<Projects.SyncMesh_MeshMonitor_Api>("mesh-monitor-api")
    .WithEnvironment("MeshMonitor__Auth__SigningKey", meshMonitorSigningKey)
    .WithEnvironment(context =>
    {
        var endpointA = natsHubA.GetEndpoint("client");
        context.EnvironmentVariables["MeshMonitor__NatsUrls__0"] = ReferenceExpression.Create($"nats://{endpointA.Property(EndpointProperty.IPV4Host)}:{endpointA.Property(EndpointProperty.Port)}");
        var endpointB = natsHubB.GetEndpoint("client");
        context.EnvironmentVariables["MeshMonitor__NatsUrls__1"] = ReferenceExpression.Create($"nats://{endpointB.Property(EndpointProperty.IPV4Host)}:{endpointB.Property(EndpointProperty.Port)}");
    })
    .WaitFor(natsHubA)
    .WaitFor(natsHubB);

// Order book demo (SyncMesh.OrderBook.Api) — commands route through
// either daemon's IPC pipe; the read model is built by polling ONLY site
// A's database. Orders placed at site B showing up here too is the
// concrete proof of mesh convergence this demo exists for — see
// docs/06-data-model.md's Order Book Example Domain section.
builder.AddProject<Projects.SyncMesh_OrderBook_Api>("orderbook-api")
    .WithEnvironment("ConnectionStrings__EventStore", $"Data Source={eventStoreAPath}")
    // Wait for serverhost-a itself so its Database.MigrateAsync() has
    // already created the schema in site-a-events.db before this
    // read-only projector starts querying the same file.
    .WaitFor(serverHostA)
    .WithEnvironment("OrderBook__Sites__0__SiteId", "site-a")
    .WithEnvironment("OrderBook__Sites__0__PipeName", "syncmesh-daemon-a")
    .WithEnvironment("OrderBook__Sites__1__SiteId", "site-b")
    .WithEnvironment("OrderBook__Sites__1__PipeName", "syncmesh-daemon-b");

builder.Build().Run();
