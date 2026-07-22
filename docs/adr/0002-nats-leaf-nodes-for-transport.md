# ADR-0002: NATS (Core + JetStream) with Leaf Nodes and Gateways as the Sync Transport

| | |
|---|---|
| Status | Accepted |
| Date | 2026-07-22 |
| Deciders | Architecture |

## Context

We need a transport connecting four tiers: local daemon → nearest server
(on-prem/WAN/cloud, config-selected) → full server mesh (peer-to-peer or
peer-to-cloud). Requirements: outbound-only connectivity from the edge (no
inbound firewall rules), durable local buffering during outages, low
operational footprint at the edge, and a topology that naturally expresses
"nearest neighbor" without bespoke routing logic.

## Decision

Use NATS as the transport at every tier:
- **Daemon ↔ nearest server**: NATS leaf node connection (daemon runs or
  embeds a local NATS server configured as a leaf, dialing out to the
  nearest cluster).
- **Server ↔ server**: NATS gateway connections (supercluster), preferring
  a hub-and-spoke shape over full mesh as node count grows.
- **Local durability**: JetStream stream with WorkQueue retention at the
  daemon, sized as a short-lived buffer, not a permanent log.

## Considered Alternatives

- **RabbitMQ + federation/shovel plugins** — mature, well-understood, but
  heavier per-node footprint for edge deployment and federation
  configuration is more operationally involved for a "nearest neighbor"
  shape than NATS leaf nodes, which were purpose-built for this pattern.
- **MQTT (e.g. Mosquitto) with persistent sessions** — good fit for
  constrained IoT-class devices, but weaker replay/delivery guarantees than
  JetStream and not intended for server-to-server mesh reconciliation.
- **Custom transport over raw AMQP 1.0** — protocol-portable across some
  brokers, but leaves us building leaf/hub topology, reconnection, and
  local buffering ourselves; NATS provides these as first-class features.
- **Kafka** — excellent ordering/replay guarantees, but heavy for an edge
  daemon (JVM footprint, ZooKeeper/KRaft operational overhead) and not
  designed for outbound-only edge connectivity in the way leaf nodes are.

## Consequences

- Positive: outbound-only dialing works behind NAT/firewalls without
  configuration; leaf nodes serve local consumers first and fall back
  transparently; single lightweight binary per node keeps edge footprint
  small; environment selection (on-prem/WAN/cloud) becomes a config value
  (connection URL + credentials), satisfying the earlier "swap via config"
  goal.
- Negative / risk: leaf-node JetStream mirror-and-forward after extended
  disconnection has known rough edges in practice — this must be
  integration-tested against our actual outage patterns before being
  trusted for correctness-critical sync (see Open Question in design doc).
  Full mesh gateway topology also gets operationally harder as site count
  and instability grow; plan for hub-and-spoke as the default starting
  shape. Full mesh is not precluded — NATS gateways support it natively as
  a topology/config choice, not a code branch — it's simply not the
  validated default until real node count/instability characteristics are
  known. Minimum-scale deployments (a standalone single server, or
  hub-and-spoke) are what's exercised first; see Open Question 4 in the
  design doc.
- Follow-up: capacity-plan the local WorkQueue cap once real recording
  session sizes/durations are known. Load-test leaf reconnect/resync
  behavior explicitly as part of Phase 3 of the implementation guide.

## Amendment (2026-07-22)

Refinements to the original decision, made during Phase 0 planning — the
transport choice above stands; these narrow how it's used. Supersedes the
"capacity-plan the local WorkQueue cap" follow-up below with a concrete
default instead of an open sizing exercise.

- **Buffer cap has a floor and a configurable ceiling, not one fixed
  number.** Floor: never discard an event before the nearest server acks
  it — that's what WorkQueue retention already gives. Ceiling: defaults to
  unbounded except by available local disk (try to keep everything until
  the disk actually runs out), configurable down to an explicit
  `MaxBytes`/`MaxAge`/`MaxMsgs` via `IOptions<T>` (see `ARCHITECTURE.md` →
  Configuration). When disk is genuinely exhausted, reject new local
  writes (`Discard: New`) rather than evict unacknowledged data — the only
  behavior consistent with the floor above.
- **Sync is one-way at the leaf-node hop**: daemon (client) → server
  (service), writes only. The leaf-node connection is never used to push a
  mirror/replica of server-side data back down to the daemon. Reads the
  local app needs are served entirely from the daemon's own local store (a
  "buffered read"), never proxied to/from the server. This one-way
  pattern is specific to the client↔service relationship (Local
  App↔Daemon, Daemon↔Server) — it does **not** extend to gateway
  connections between servers, which are two-way: every connected server
  both publishes its own events and applies incoming events from peers,
  converging to the same fully-replicated history (full eventual
  replication, not a consensus/quorum-voting mechanism — no write blocks
  on peer acknowledgment).
- **Gateway topology is fully flexible, with no architectural minimum or
  maximum on server/site/gateway count.** Common patterns include: full
  mesh *within* a site (reliable local/LAN connectivity makes this the
  simplest shape), and a single/limited designated gateway server per site
  carrying *cross-site* links instead of full mesh across every server at
  every site (bounds WAN-crossing connection count). Full mesh extending
  all the way to cloud/remote sites is equally valid — the designated-
  gateway pattern is a preference for bounding connection count, not a
  restriction, and neither pattern forecloses the other. No on-prem tier
  is required at all: a daemon connecting straight to a cloud server, with
  zero on-prem servers, is a fully valid shape too. Whatever the physical
  links, every server everywhere still converges to the same
  fully-replicated history, since reconciliation doesn't care which links
  carried the data. See design doc §4.3–4.4, Open Question 4, and
  `docs/08-deployment-models.md` for diagrams of these shapes.
- **Standalone (zero peer connections) — including a daemon with no
  nearest server at all — is a first-class, permanent topology**, not a
  bootstrapping step — see design doc §4.2–4.4 and Open Question 4. A
  standalone site's later reconciliation with others, if ever needed, may
  be an offline/batch mechanism rather than a live gateway connection;
  that mechanism itself is undesigned and is a separate future decision.
- **All connections — leaf, gateway, and the Tier X tunnel/relay — use TLS
  and authenticate with registered service credentials scoped to the
  daemon/server instance, never end-user identity/permissions.** See
  design doc §4.4–4.5 and Open Question 5 (the full tunnel security review
  remains required before production; this is the transport-level
  baseline it builds on).

## Related

`docs/00-design-document.md` §4.2–4.5, `docs/adr/0003-hybrid-logical-clock-ordering.md`,
`docs/adr/0004-separate-tunnel-from-event-mesh.md`
