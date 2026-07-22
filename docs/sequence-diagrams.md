# Sequence Diagrams

PlantUML sequence diagrams for the key flows across the recording, sync,
and monitoring/tunnel tiers. Render with any PlantUML renderer (VS Code
PlantUML extension, plantuml.com server, or local `plantuml.jar`) — most
tools that support PlantUML also render fenced ` ```plantuml ` code blocks
directly out of Markdown.

## Event Recording Flow — Local App to Nearest Server

```plantuml
@startuml event-recording-flow
title Event Recording Flow — Local App to Nearest Server

actor Operator
participant "Local App" as App
participant "Local Daemon" as Daemon
database "Local SQLite\n(recording session)" as LocalDb
participant "Local JetStream\n(WorkQueue)" as JS
participant "Leaf Connection" as Leaf
participant "Nearest Server\nSync Service" as Server
database "Server Event Store\n(Postgres/SQL Server)" as ServerDb

Operator -> App: Perform recordable action
App -> Daemon: Send event (local IPC)
activate Daemon
Daemon -> Daemon: Assign GlobalEventId + HLC
Daemon -> LocalDb: Append event (durable for session)
Daemon -> JS: Publish to local stream
JS -> Leaf: Forward (outbound-only dial)
deactivate Daemon

alt Nearest server reachable
    Leaf -> Server: Deliver event
    Server -> Server: Idempotent apply check (GlobalEventId)
    Server -> ServerDb: Append (system of record)
    Server --> Leaf: Ack
    Leaf --> JS: Ack received, remove from local WorkQueue
else Nearest server unreachable
    JS -> JS: Retain event locally (bounded by MaxAge/MaxMsgs)
    note right of JS: Flush automatically on reconnect.\nSee ADR-0002 re: reconnect-sync risk.
end

@enduml
```

## Server Mesh Reconciliation — HLC-Ordered, Idempotent Apply

```plantuml
@startuml sync-nearest-neighbor
title Server Mesh Reconciliation — HLC-Ordered, Idempotent Apply

participant "Server A\n(received event locally)" as A
participant "NATS Gateway /\nSupercluster" as Gateway
participant "Server B" as B
database "Server B\nEvent Store" as BDb
participant "Server C" as C
database "Server C\nEvent Store" as CDb

A -> Gateway: Publish event (events.<originSiteId>.<streamId>)
Gateway -> B: Deliver (interest-based routing)
Gateway -> C: Deliver (interest-based routing)

group Server B apply
    B -> BDb: Exists GlobalEventId? 
    alt Not yet applied
        B -> BDb: Insert event
        B -> B: Merge HLC (received, local)
    else Already applied
        B -> B: No-op (idempotent — at-least-once delivery expected)
    end
end

group Server C apply
    C -> CDb: Exists GlobalEventId?
    alt Not yet applied
        C -> CDb: Insert event
        C -> C: Merge HLC (received, local)
    else Already applied
        C -> C: No-op
    end
end

note over A, C
  Ordering is reconstructed at replay time using
  (HlcPhysicalTicks, HlcLogicalCounter) — never assumed
  from delivery order across the gateway/supercluster.
end note

@enduml
```

## Remote Monitoring / Tunnel — Direct-First, Relay Fallback

```plantuml
@startuml remote-monitoring-tunnel-fallback
title Remote Monitoring / Tunnel — Direct-First, Relay Fallback

actor "Remote User" as User
participant "Tunnel Client" as Client
participant "Local Daemon\n(Tunnel Agent)" as Daemon
participant "Nearest Server\n(Relay)" as Relay

User -> Client: Request monitor / interactive session
Client -> Daemon: Attempt direct connection

alt Direct connection succeeds
    Daemon --> Client: Session established directly
    note right of Daemon
      Preferred path — lowest latency,
      no dependency on relay availability.
    end note
else Direct connection blocked (firewall/NAT)
    Client -> Relay: Request relay to nearest server
    Relay -> Daemon: Establish relay leg (outbound from daemon side)
    Relay --> Client: Session established via relay
    note over Client, Relay
      Separate failure domain from event sync.
      See ADR-0004 — a relay outage must not
      affect recording durability or event delivery.
    end note
end

== Passive monitoring (always, regardless of tunnel path) ==
Daemon -> Relay: Publish monitor.<siteId>.<instanceId>.* (NATS pub/sub)
Relay -> Client: Delivered via existing mesh interest-graph routing

@enduml
```
