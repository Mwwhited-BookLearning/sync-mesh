# Deployment Models

Illustrative deployment shapes this architecture supports. None of these are
mutually exclusive or privileged over another — topology is a
deployment/configuration decision, not a code branch (see
`docs/00-design-document.md` §4.2–4.4, Open Question 4, and
`docs/adr/0002-nats-leaf-nodes-for-transport.md`). There is no architectural
minimum or maximum on server, site, or gateway count; the patterns below are
common shapes, not an exhaustive list.

Rendered with any PlantUML renderer (VS Code PlantUML extension,
plantuml.com server, or local `plantuml.jar`) — most tools that support
PlantUML also render fenced ` ```plantuml ` code blocks directly out of
Markdown, per this project's diagram convention (`CLAUDE.md`).

## 1. Client isolated (no nearest server)

A daemon with no nearest server configured or reachable at all. This is a
**valid, potentially permanent** deployment mode, not just a temporary
outage to tolerate — e.g. an air-gapped recording station. Buffered local
read/write both keep working; the write buffer grows until local disk is
exhausted (§4.2's floor/ceiling model applied indefinitely), then rejects
new writes rather than evicting unacknowledged data.

```plantuml
@startuml client-isolated
title Deployment: Client Isolated (No Nearest Server)

node "Recording Station" {
  component "Local App" as app
  component "Local Daemon" as daemon
  database "Local SQLite\n(disk-bound buffer)" as db
}

app --> daemon : write (local IPC)
daemon --> db : append (min: none needed \nto ack — max: until disk full)
daemon --> app : buffered read

note bottom of daemon
  No nearest server configured or reachable.
  Valid permanent mode, not just an outage.
  On disk exhaustion: reject new writes
  (Discard: New), never evict unacked data.
end note

@enduml
```

## 2. Client → on-prem server

The common case: a daemon at a site syncs to an on-prem nearest server at
the same site over a NATS leaf connection.

```plantuml
@startuml client-to-onprem
title Deployment: Client → On-Prem Server

node "Site A" {
  node "Recording Station" {
    component "Local App" as app
    component "Local Daemon" as daemon
    database "Local SQLite" as localdb
  }
  node "On-Prem Server" {
    component "ServerHost" as server
    database "PostgreSQL /\nSQL Server" as serverdb
  }
}

app --> daemon : write (local IPC)
daemon --> localdb : append
daemon ..> server : NATS leaf node\n(outbound-only, TLS,\nservice credential)
server --> serverdb : idempotent apply

note right of daemon
  Sync is one-way: daemon → server, writes only.
  No mirror of server data comes back down.
end note

@enduml
```

## 3. Client → cloud server (no on-prem tier)

No on-prem server is required at all — a daemon can connect directly to a
cloud-hosted nearest server. This is a fully valid shape, not a degraded
case of pattern 2.

```plantuml
@startuml client-to-cloud
title Deployment: Client → Cloud Server (No On-Prem Tier)

node "Site A" {
  node "Recording Station" {
    component "Local App" as app
    component "Local Daemon" as daemon
    database "Local SQLite" as localdb
  }
}

cloud "Cloud Region" {
  node "Cloud Server" {
    component "ServerHost" as server
    database "PostgreSQL /\nSQL Server" as serverdb
  }
}

app --> daemon : write (local IPC)
daemon --> localdb : append
daemon ..> server : NATS leaf node over WAN\n(outbound-only, TLS,\nservice credential)
server --> serverdb : idempotent apply

@enduml
```

## 4. Standalone server (zero peers)

A single server with no gateway connections to any peer — first-class and
permanent, not a bootstrapping step toward a mesh. Serves one or more
daemons directly.

```plantuml
@startuml standalone-server
title Deployment: Standalone Server (Zero Peers)

node "Site A" {
  node "Recording Station 1" {
    component "Daemon 1" as d1
  }
  node "Recording Station 2" {
    component "Daemon 2" as d2
  }
  node "Server" {
    component "ServerHost" as server
    database "Event Store" as db
  }
}

d1 ..> server : NATS leaf
d2 ..> server : NATS leaf
server --> db : system of record

note bottom of server
  No gateway connections configured.
  No minimum node count — this is a
  first-class, permanent topology.
  Later reconciliation with other sites,
  if ever needed, may be offline/batch,
  not assumed to be "gateways, later."
end note

@enduml
```

## 5. Intra-site full mesh, inter-site limited gateway

A common multi-site pattern: servers within a site are fully meshed
(reliable LAN), while cross-site links go through a single/limited
designated gateway server per site — bounding the number of WAN-crossing
connections. Every server everywhere still converges to the same
fully-replicated history (two-way sync); this pattern only changes *how
many* connections carry that convergence.

```plantuml
@startuml intrasite-mesh-limited-gateway
title Deployment: Intra-Site Full Mesh, Inter-Site Limited Gateway

node "Site A" {
  node "Server A1 (gateway)" as a1
  node "Server A2" as a2
  node "Server A3" as a3
}

node "Site B" {
  node "Server B1 (gateway)" as b1
  node "Server B2" as b2
}

cloud "Cloud Region" {
  node "Cloud Server (gateway)" as c1
}

a1 <--> a2 : gateway (full mesh)
a1 <--> a3 : gateway (full mesh)
a2 <--> a3 : gateway (full mesh)
b1 <--> b2 : gateway (full mesh)

a1 <..> b1 : inter-site gateway\n(designated per site)
a1 <..> c1 : inter-site gateway\n(designated per site)
b1 <..> c1 : inter-site gateway\n(designated per site)

note bottom
  Non-gateway servers (A2, A3, B2) don't hold
  their own cross-site links — they still
  converge to the full mesh history via their
  site's designated gateway + intra-site mesh.
end note

@enduml
```

## 6. Full mesh everywhere (including cloud)

Full mesh is equally valid extending all the way to cloud/remote sites
directly — the designated-gateway pattern above is a preference for
bounding connection count, not a restriction.

```plantuml
@startuml full-mesh-everywhere
title Deployment: Full Mesh Everywhere (Including Cloud)

node "Site A" {
  node "Server A1" as a1
  node "Server A2" as a2
}

node "Site B" {
  node "Server B1" as b1
}

cloud "Cloud Region" {
  node "Cloud Server" as c1
}

a1 <--> a2 : gateway
a1 <--> b1 : gateway
a1 <--> c1 : gateway
a2 <--> b1 : gateway
a2 <--> c1 : gateway
b1 <--> c1 : gateway

note bottom
  Every server gateway-connects to every
  other server, on-prem and cloud alike.
  No designated-gateway bottleneck — a
  valid choice when connection count isn't
  a concern at the current site/server count.
end note

@enduml
```
