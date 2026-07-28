var builder = DistributedApplication.CreateBuilder(args);

// Which of docs/08-deployment-models.md's shapes to stand up — selected via
// launch profile (Properties/launchSettings.json), e.g. `dotnet run
// --project src/SyncMesh.AppHost --launch-profile client-isolated`. Smart-
// defaults to the existing two-site Order Book demo topology (see
// "order-book-demo" case below) so a bare `dotnet run` with no profile
// keeps behaving exactly as it always has — see
// docs/10-running-deployment-models.md for the full model reference and
// ARCHITECTURE.md for why this is structured as 4 reusable local functions
// plus a flat switch, not a more generic topology builder.
var deploymentModel = builder.Configuration["DeploymentModel"] ?? "order-book-demo";

var dataDir = Path.Combine(builder.AppHostDirectory, ".data");
Directory.CreateDirectory(dataDir);

// Every container this run creates gets the same com.docker.compose.project
// label — Docker Desktop groups containers sharing this label into one
// nested "project" view, the same as if they'd been started via `docker
// compose`, even though Aspire isn't using compose to run them. Purely a
// visual/organizational label — doesn't change how anything actually runs.
var dockerProjectLabel = $"sync-mesh-{deploymentModel}";

// --- Reusable resource-block shapes, used by 2+ of the selectable models
// below. The "order-book-demo" case deliberately does NOT go through
// these — it stays byte-for-byte what it always was, so this restructuring
// carries zero regression risk for the one topology the Order Book demo
// and Mesh Monitor dashboard both depend on.

(IResourceBuilder<ContainerResource> Hub, IResourceBuilder<ContainerResource> Leaf) AddNatsHubLeafPair(string suffix)
{
    var hub = builder.AddContainer($"nats-hub-{suffix}", "nats", "2-alpine")
        .WithBindMount("nats-config/hub.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
        .WithArgs("-c", "/etc/nats/nats-server.conf")
        .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
        .WithEndpoint(targetPort: 7422, name: "leafnode", scheme: "tcp")
        .WithContainerRuntimeArgs("--label", $"com.docker.compose.project={dockerProjectLabel}");

    var leaf = builder.AddContainer($"nats-leaf-{suffix}", "nats", "2-alpine")
        .WithBindMount($"nats-config/leaf-{suffix}.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
        .WithArgs("-c", "/etc/nats/nats-server.conf")
        .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
        .WithContainerRuntimeArgs("--label", $"com.docker.compose.project={dockerProjectLabel}")
        .WaitFor(hub);

    return (hub, leaf);
}

// No leafnodes block — these models don't need a leaf/hub split at all;
// server-mesh replication is point-to-point at the application layer
// (ServerMeshOptions.Peers / MeshForwarder dialing each peer's own NATS
// URL directly), not native NATS gateway clustering. Matches
// deploy/nats-config/plain-jetstream.conf's own reasoning.
IResourceBuilder<ContainerResource> AddStandaloneNats(string name) =>
    builder.AddContainer(name, "nats", "2-alpine")
        .WithBindMount("nats-config/plain-jetstream.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
        .WithArgs("-c", "/etc/nats/nats-server.conf", "--name", name)
        .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
        .WithContainerRuntimeArgs("--label", $"com.docker.compose.project={dockerProjectLabel}");

// SQLite per instance (ADR-0001's Amendment), explicit SiteId/InstanceId
// (no usable smart default once more than one instance exists), and
// EndpointProperty.IPV4Host everywhere a dynamic NATS URL is built — using
// .Host instead reproduces a dual-stack "localhost resolves ::1 first"
// connection failure already hit and fixed this session.
IResourceBuilder<ProjectResource> AddServerHost(
    string name,
    IResourceBuilder<ContainerResource> nats,
    string siteId,
    string instanceId,
    string dbFileName,
    (string SiteId, IResourceBuilder<ContainerResource> Nats)[] peers,
    int? agentPort = null,
    int? clientPort = null)
{
    var dbPath = Path.Combine(dataDir, $"{dbFileName}.db");
    var server = builder.AddProject<Projects.SyncMesh_ServerHost>(name)
        .WithEnvironment("ConnectionStrings__EventStore", $"Data Source={dbPath}")
        .WithEnvironment("EventStore__Provider", "Sqlite")
        .WithEnvironment("ServerHost__Monitor__SiteId", siteId)
        .WithEnvironment("ServerHost__Monitor__InstanceId", instanceId)
        .WithEnvironment(context =>
        {
            var endpoint = nats.GetEndpoint("client");
            context.EnvironmentVariables["ServerHost__Nats__Url"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.IPV4Host)}:{endpoint.Property(EndpointProperty.Port)}");
        })
        .WaitFor(nats);

    for (var i = 0; i < peers.Length; i++)
    {
        var index = i;
        var peer = peers[i];
        server = server.WithEnvironment(context =>
        {
            var peerEndpoint = peer.Nats.GetEndpoint("client");
            context.EnvironmentVariables[$"ServerHost__Mesh__Peers__{index}__SiteId"] = peer.SiteId;
            context.EnvironmentVariables[$"ServerHost__Mesh__Peers__{index}__Url"] = ReferenceExpression.Create($"nats://{peerEndpoint.Property(EndpointProperty.IPV4Host)}:{peerEndpoint.Property(EndpointProperty.Port)}");
        });
    }

    // Tunnel listeners are plain TCP, not Aspire-managed dynamic endpoints
    // — every instance past the first on this one machine needs an
    // explicit, non-colliding literal port pair (same convention the
    // order-book-demo topology already uses for its second site).
    if (agentPort is not null)
    {
        server = server.WithEnvironment("ServerHost__Tunnel__AgentListenPort", agentPort.Value.ToString());
    }
    if (clientPort is not null)
    {
        server = server.WithEnvironment("ServerHost__Tunnel__ClientListenPort", clientPort.Value.ToString());
    }

    return server;
}

IResourceBuilder<ProjectResource> AddDaemon(
    string name,
    IResourceBuilder<ContainerResource> nats,
    string siteId,
    string instanceId,
    string pipeName,
    int? directListenPort = null,
    string? relayUrl = null)
{
    var daemon = builder.AddProject<Projects.SyncMesh_Daemon>(name)
        .WithEnvironment("Daemon__SiteId", siteId)
        .WithEnvironment("Daemon__InstanceId", instanceId)
        .WithEnvironment("Daemon__IpcPipeName", pipeName)
        .WithEnvironment(context =>
        {
            var endpoint = nats.GetEndpoint("client");
            context.EnvironmentVariables["Daemon__Nats__Url"] = ReferenceExpression.Create($"nats://{endpoint.Property(EndpointProperty.IPV4Host)}:{endpoint.Property(EndpointProperty.Port)}");
        })
        .WaitFor(nats);

    if (directListenPort is not null)
    {
        daemon = daemon.WithEnvironment("Daemon__Tunnel__DirectListenPort", directListenPort.Value.ToString());
    }
    if (relayUrl is not null)
    {
        daemon = daemon.WithEnvironment("Daemon__Tunnel__RelayUrl", relayUrl);
    }

    return daemon;
}

switch (deploymentModel)
{
    // --- 1. Client isolated (no nearest server) — docs/08-deployment-
    // models.md §1. A daemon with nothing ever answering
    // server.apply.request: runs fine forever, just retries/buffers.
    // There's no "disabled NATS" mode — this is that behavior, not a
    // special case of it.
    case "client-isolated":
    {
        var nats = AddStandaloneNats("nats-isolated");
        AddDaemon("daemon", nats, "site-a", "daemon-a", "syncmesh-daemon");
        break;
    }

    // --- 2. Client -> on-prem server — docs/08-deployment-models.md §2.
    case "client-onprem":
    {
        var (hub, leaf) = AddNatsHubLeafPair("onprem");
        AddServerHost("serverhost", hub, "site-a", "server-a", "onprem-events", peers: []);
        AddDaemon("daemon", leaf, "site-a", "daemon-a", "syncmesh-daemon");
        break;
    }

    // --- 3. Client -> cloud server (no on-prem tier) — docs/08-deployment-
    // models.md §3. Structurally identical to client-onprem — the on-prem
    // vs. cloud distinction is where the server logically sits, not
    // anything this topology graph expresses differently.
    case "client-cloud":
    {
        var (hub, leaf) = AddNatsHubLeafPair("cloud");
        AddServerHost("serverhost", hub, "site-a", "server-a", "cloud-events", peers: []);
        AddDaemon("daemon", leaf, "site-a", "daemon-a", "syncmesh-daemon");
        break;
    }

    // --- 4. Standalone server (zero peers) — docs/08-deployment-models.md
    // §4. No leaf/hub split needed (no cross-site concern to demonstrate);
    // the daemon points at the same standalone NATS the server uses.
    case "standalone-server":
    {
        var nats = AddStandaloneNats("nats-standalone");
        AddServerHost("serverhost", nats, "site-a", "server-a", "standalone-events", peers: []);
        AddDaemon("daemon", nats, "site-a", "daemon-a", "syncmesh-daemon");
        break;
    }

    // --- 5. Intra-site full mesh, inter-site limited gateway —
    // docs/08-deployment-models.md §5. Three nodes, B is the designated
    // gateway: A<->B and B<->C are peered, A<->C is not — the only
    // difference from full-mesh below. No daemons (server-mesh only).
    case "intra-site-mesh":
    {
        var natsA = AddStandaloneNats("nats-mesh-a");
        var natsB = AddStandaloneNats("nats-mesh-b");
        var natsC = AddStandaloneNats("nats-mesh-c");

        AddServerHost("serverhost-a", natsA, "site-a", "server-a", "mesh-a-events",
            peers: [("site-b", natsB)]);
        AddServerHost("serverhost-b", natsB, "site-b", "server-b", "mesh-b-events",
            peers: [("site-a", natsA), ("site-c", natsC)],
            agentPort: 7788, clientPort: 7789);
        AddServerHost("serverhost-c", natsC, "site-c", "server-c", "mesh-c-events",
            peers: [("site-b", natsB)],
            agentPort: 7798, clientPort: 7799);
        break;
    }

    // --- 6. Full mesh everywhere — docs/08-deployment-models.md §6. Same
    // three nodes as intra-site-mesh, but every node peers every other —
    // no designated-gateway bottleneck. No daemons (server-mesh only).
    case "full-mesh":
    {
        var natsA = AddStandaloneNats("nats-full-a");
        var natsB = AddStandaloneNats("nats-full-b");
        var natsC = AddStandaloneNats("nats-full-c");

        AddServerHost("serverhost-a", natsA, "site-a", "server-a", "full-a-events",
            peers: [("site-b", natsB), ("site-c", natsC)]);
        AddServerHost("serverhost-b", natsB, "site-b", "server-b", "full-b-events",
            peers: [("site-a", natsA), ("site-c", natsC)],
            agentPort: 7788, clientPort: 7789);
        AddServerHost("serverhost-c", natsC, "site-c", "server-c", "full-c-events",
            peers: [("site-a", natsA), ("site-b", natsB)],
            agentPort: 7798, clientPort: 7799);
        break;
    }

    // --- Default: the Order Book demo's two-site full mesh — unchanged
    // from before this file supported multiple models. Each site is a
    // complete Tier 0-3 stack (daemon + its own nearest server), and the
    // two servers are peered directly (ADR-0002's Phase 3 Amendment:
    // point-to-point, not native NATS gateway/supercluster). This is what
    // makes mesh convergence something you can actually watch happen via
    // the Order Book demo (docs/06-data-model.md's Order Book Example
    // Domain section) rather than only something proven in tests.
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
    case "order-book-demo":
    default:
    {
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
            .WithEndpoint(targetPort: 7422, name: "leafnode", scheme: "tcp")
            .WithContainerRuntimeArgs("--label", $"com.docker.compose.project={dockerProjectLabel}");

        var natsLeafA = builder.AddContainer("nats-leaf-a", "nats", "2-alpine")
            .WithBindMount("nats-config/leaf-a.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
            .WithArgs("-c", "/etc/nats/nats-server.conf")
            .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
            .WithContainerRuntimeArgs("--label", $"com.docker.compose.project={dockerProjectLabel}")
            .WaitFor(natsHubA);

        var natsHubB = builder.AddContainer("nats-hub-b", "nats", "2-alpine")
            .WithBindMount("nats-config/hub.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
            .WithArgs("-c", "/etc/nats/nats-server.conf")
            .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
            .WithEndpoint(targetPort: 7422, name: "leafnode", scheme: "tcp")
            .WithContainerRuntimeArgs("--label", $"com.docker.compose.project={dockerProjectLabel}");

        var natsLeafB = builder.AddContainer("nats-leaf-b", "nats", "2-alpine")
            .WithBindMount("nats-config/leaf-b.conf", "/etc/nats/nats-server.conf", isReadOnly: true)
            .WithArgs("-c", "/etc/nats/nats-server.conf")
            .WithEndpoint(targetPort: 4222, name: "client", scheme: "tcp")
            .WithContainerRuntimeArgs("--label", $"com.docker.compose.project={dockerProjectLabel}")
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

        var meshMonitorApi = builder.AddProject<Projects.SyncMesh_MeshMonitor_Api>("mesh-monitor-api")
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

        // web/mesh-monitor's own live dev server (npm run dev, hot reload),
        // orchestrated as its own Aspire resource rather than only the
        // pre-built static bundle mesh-monitor-api serves from wwwroot (that
        // serving path is untouched — still what a plain `dotnet run`/
        // publish of just SyncMesh.MeshMonitor.Api gets, no Aspire
        // required). VITE_MESHMONITOR_API_URL carries the backend's actual
        // (dynamically-assigned) endpoint into vite.config.ts's own Node
        // process — read via plain `process.env`, not `import.meta.env`
        // (that's for application code in the browser; vite.config.ts runs
        // in Node at dev-server-startup, where the literal env var is
        // already there) — see UI-ARCHITECTURE.md.
        builder.AddViteApp("mesh-monitor-web", "../../web/mesh-monitor")
            .WithReference(meshMonitorApi)
            .WithEnvironment("VITE_MESHMONITOR_API_URL", meshMonitorApi.GetEndpoint("http"))
            .WaitFor(meshMonitorApi);

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

        break;
    }
}

builder.Build().Run();
