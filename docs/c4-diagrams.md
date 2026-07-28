# C4 Diagrams

C4 model diagrams that are **cross-cutting or not yet owned by a single
BDD feature** — per-feature component diagrams now live alongside their
owning feature under `docs/bdd/design/*.md` (see e.g. "Component Diagram
— Local Daemon" in `docs/bdd/design/local-durability.md`) so each
feature's design stands on its own. This file keeps only: the whole-system
Context and Container diagrams (span every feature, owned by none), and
the Mesh Monitor Dashboard's component diagram (not yet feature-owned —
see ADR-0005, `WORKPLAN.md` → "Mesh Monitor Dashboard").

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

## Component Diagram — Mesh Monitor Dashboard (C4 Level 3)

See ADR-0005 (SPA built, per its Amendment) and ADR-0009 (ticket-based
auth in front of both the hub and the REST endpoint — full detail,
including the auth-specific components and the ticket-exchange sequence,
in `docs/bdd/design/mesh-monitor-ticket-auth.md`, not repeated here).

```plantuml
@startuml component-mesh-monitor
!include https://raw.githubusercontent.com/plantuml-stdlib/C4-PlantUML/master/C4_Component.puml

title Component Diagram — Mesh Monitor Dashboard (SyncMesh.MeshMonitor.Api)

Container_Boundary(meshMonitorApi, "Mesh Monitor Dashboard — Backend") {
    Component(monitorSubscriber, "Monitor Subscriber", "NATS client, BackgroundService", "Subscribes to monitor.>, parses DaemonStatus/ServerStatus by NodeKind")
    Component(topologyStore, "Topology Store", "In-memory ConcurrentDictionary", "Latest-known snapshot per (siteId, instanceId); non-durable by design")
    Component(topologyApi, "Topology REST Endpoint", "Minimal API, Bearer-or-Ticket", "GET /api/topology — snapshot for a freshly opened tab")
    Component(monitorHub, "SignalR Hub", "MeshMonitorHub, Bearer-or-Ticket", "Server-push only; broadcasts NodeUpdated to connected tabs")
    Component(auth, "Auth subsystem", "JwtBearer + Ticket scheme", "See docs/bdd/design/mesh-monitor-ticket-auth.md's Component Diagram")
    Component(staticFiles, "Static SPA Host", "UseStaticFiles + MapFallbackToFile", "Serves web/mesh-monitor's build output")
}

Component(spa, "Mesh Monitor SPA", "Vue 3 + Element Plus + vis-network", "web/mesh-monitor — see UI-ARCHITECTURE.md")

Rel(monitorSubscriber, topologyStore, "Upserts parsed node")
Rel(monitorSubscriber, monitorHub, "Pushes NodeUpdated on every message")
Rel(topologyApi, topologyStore, "Reads snapshot")
Rel(topologyApi, auth, "Authenticate via")
Rel(monitorHub, auth, "Authenticate via")
Rel(spa, topologyApi, "Initial load")
Rel(spa, monitorHub, "Live updates")
Rel(staticFiles, spa, "Serves build output")

@enduml
```
