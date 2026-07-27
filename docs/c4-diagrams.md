# C4 Diagrams

C4 model diagrams for the distributed event-sourced recording & sync mesh.
Rendered with PlantUML + the [C4-PlantUML](https://github.com/plantuml-stdlib/C4-PlantUML)
include library (pulled from GitHub at render time — vendor a local copy of
`C4_Context.puml`/`C4_Container.puml`/`C4_Component.puml` if you need offline
rendering).

Render with any PlantUML renderer (VS Code PlantUML extension, plantuml.com
server, or local `plantuml.jar`) — most tools that support PlantUML also
render fenced ` ```plantuml ` code blocks directly out of Markdown.

## System Context (C4 Level 1)

```plantuml
@startuml context
!include https://raw.githubusercontent.com/plantuml-stdlib/C4-PlantUML/master/C4_Context.puml

title System Context — Distributed Event-Sourced Recording & Sync Mesh

Person(operator, "Local Operator", "Uses the local application to record events")
Person(remoteUser, "Remote Monitoring User", "Observes or interactively accesses a recording instance remotely")

System_Boundary(recordingSite, "Recording Site") {
    System(localApp, "Local Application", "Records events during a session")
    System(daemon, "Local Daemon", "Durable buffer during recording; forwards to nearest server")
}

System(nearestServer, "Nearest Server", "On-prem, WAN, or cloud — selected by configuration")
System(serverMesh, "Server Mesh", "Full set of servers reconciling events into one ordered history")
System(relay, "Nearest-Server Relay", "Fallback path for monitoring/tunnel when direct access is blocked")

Rel(operator, localApp, "Records via")
Rel(localApp, daemon, "Sends events to (local IPC)")
Rel(daemon, nearestServer, "Forwards events to (NATS leaf node, outbound-only)")
Rel(nearestServer, serverMesh, "Reconciles with (NATS gateway / supercluster)")
Rel(remoteUser, daemon, "Monitors / tunnels directly to, when reachable")
Rel(remoteUser, relay, "Falls back to, when firewalls block direct access")
Rel(relay, nearestServer, "Relays through")

@enduml
```

## Container Diagram (C4 Level 2)

```plantuml
@startuml container
!include https://raw.githubusercontent.com/plantuml-stdlib/C4-PlantUML/master/C4_Container.puml

title Container Diagram — Recording Site, Nearest Server, Server Mesh

Person(operator, "Local Operator")
Person(remoteUser, "Remote Monitoring User")

System_Boundary(recordingSite, "Recording Site") {
    Container(localApp, "Local Application", "Native app", "Captures user actions as domain events")
    Container(daemon, "Local Daemon", ".NET service / worker", "Owns local durability; hosts embedded NATS leaf node")
    ContainerDb(localBuffer, "Local Event Buffer", "SQLite + NATS JetStream (WorkQueue retention)", "Durable only during active recording session")
}

System_Boundary(nearestServerBoundary, "Nearest Server (config-selected: on-prem / WAN / cloud)") {
    Container(natsHub, "NATS Cluster Node", "nats-server", "Leaf node terminus; gateway connection to server mesh")
    Container(syncService, "Sync/Apply Service", ".NET service", "Idempotent apply, HLC merge, dedupe by GlobalEventId")
    ContainerDb(serverStore, "Event Store", "EF Core + PostgreSQL/SQL Server", "System of record; HLC-ordered replay")
}

System_Boundary(serverMeshBoundary, "Server Mesh") {
    Container(peerServer1, "Peer Server", "Same shape as Nearest Server", "On-prem or cloud peer")
    Container(peerServer2, "Peer Server", "Same shape as Nearest Server", "On-prem or cloud peer")
}

Container(tunnelRelay, "Tunnel/Monitoring Relay", "frp / chisel / overlay network", "Separate failure domain from event sync")

Person(meshOperator, "Mesh Operator")
Container(meshMonitor, "Mesh Monitor Dashboard", "ASP.NET Core + SignalR + SPA (see ADR-0005)", "Read-only, mesh-wide topology view built from monitor.* telemetry")

Rel(operator, localApp, "Uses")
Rel(localApp, daemon, "Sends events via", "named pipe / gRPC (local IPC)")
Rel(daemon, localBuffer, "Buffers into")
Rel(daemon, natsHub, "Forwards via", "NATS leaf connection (outbound-only)")
Rel(natsHub, syncService, "Delivers to")
Rel(syncService, serverStore, "Writes via EF Core")
Rel(natsHub, peerServer1, "Gateway / supercluster")
Rel(natsHub, peerServer2, "Gateway / supercluster")
Rel(remoteUser, daemon, "Direct monitor/tunnel, when reachable")
Rel(remoteUser, tunnelRelay, "Fallback relay, when blocked")
Rel(tunnelRelay, natsHub, "Relays through nearest server")
Rel(meshMonitor, natsHub, "Subscribes to monitor.> (hub side, read-only)")
Rel(meshOperator, meshMonitor, "Views mesh topology via browser")

@enduml
```

Note: `meshMonitor` connects to *a* hub — in a multi-site mesh it observes
whichever node it's pointed at; monitor subjects cross leaf/gateway
boundaries the same way event-sync subjects do (§4.5), so one dashboard
instance can see the whole reachable mesh from a single connection point.

## Component Diagram — Local Daemon (C4 Level 3)

```plantuml
@startuml component-daemon
!include https://raw.githubusercontent.com/plantuml-stdlib/C4-PlantUML/master/C4_Component.puml

title Component Diagram — Local Daemon

Container_Boundary(daemon, "Local Daemon") {
    Component(ipcListener, "IPC Listener", "Named pipe / gRPC server", "Accepts events from the local app")
    Component(eventWriter, "Local Event Writer", "EF Core + SQLite", "Appends incoming events; assigns HLC + GlobalEventId")
    Component(hlcGen, "HLC Generator", "C# component", "Assigns/merges hybrid logical clocks")
    Component(leafPublisher, "Leaf Publisher", "NATS client", "Publishes buffered events to local JetStream stream")
    Component(jetstream, "Local JetStream Stream", "NATS JetStream, WorkQueue retention", "Short-lived durable buffer")
    Component(leafConn, "Leaf Node Connection", "Embedded nats-server (leaf mode)", "Outbound-only connection to nearest server")
    Component(monitorPublisher, "Monitor Publisher", "NATS client", "Publishes telemetry to monitor.* subjects")
    Component(tunnelAgent, "Tunnel Agent", "frp/chisel client or overlay agent", "Attempts direct connectivity; else awaits relay")
}

Rel(ipcListener, eventWriter, "Passes captured event")
Rel(eventWriter, hlcGen, "Requests next HLC value")
Rel(eventWriter, leafPublisher, "Hands off event for forwarding")
Rel(leafPublisher, jetstream, "Publishes into")
Rel(jetstream, leafConn, "Delivered via, once acked upstream, entry removed (WorkQueue)")
Rel(eventWriter, monitorPublisher, "Emits status/metrics")
Rel(monitorPublisher, leafConn, "Publishes monitor.* subjects via")
Rel(tunnelAgent, leafConn, "Signals control state via tunnel.* subjects (separate from event subjects)")

@enduml
```

## Component Diagram — Mesh Monitor Dashboard (C4 Level 3)

See ADR-0005. Backend only today — the SPA component below is planned, not
yet built (no `web/mesh-monitor` frontend exists), and there is no
authentication component in front of the hub or REST endpoint yet.

```plantuml
@startuml component-mesh-monitor
!include https://raw.githubusercontent.com/plantuml-stdlib/C4-PlantUML/master/C4_Component.puml

title Component Diagram — Mesh Monitor Dashboard (SyncMesh.MeshMonitor.Api)

Container_Boundary(meshMonitorApi, "Mesh Monitor Dashboard — Backend") {
    Component(monitorSubscriber, "Monitor Subscriber", "NATS client, BackgroundService", "Subscribes to monitor.>, parses DaemonStatus/ServerStatus by NodeKind")
    Component(topologyStore, "Topology Store", "In-memory ConcurrentDictionary", "Latest-known snapshot per (siteId, instanceId); non-durable by design")
    Component(topologyApi, "Topology REST Endpoint", "Minimal API", "GET /api/topology — snapshot for a freshly opened tab")
    Component(monitorHub, "SignalR Hub", "MeshMonitorHub", "Server-push only; broadcasts NodeUpdated to connected tabs")
    Component(staticFiles, "Static SPA Host", "UseStaticFiles + MapFallbackToFile", "Serves web/mesh-monitor's build output — not yet built")
}

Component(spa, "Mesh Monitor SPA", "Planned — Vue", "Not yet implemented; see ADR-0005 Consequences") #line.dashed

Rel(monitorSubscriber, topologyStore, "Upserts parsed node")
Rel(monitorSubscriber, monitorHub, "Pushes NodeUpdated on every message")
Rel(topologyApi, topologyStore, "Reads snapshot")
Rel(spa, topologyApi, "Initial load")
Rel(spa, monitorHub, "Live updates")
Rel(staticFiles, spa, "Serves build output (once it exists)")

@enduml
```
