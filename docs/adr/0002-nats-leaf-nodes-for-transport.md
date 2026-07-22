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

## Related

`docs/00-design-document.md` §4.2–4.4, `docs/adr/0003-hybrid-logical-clock-ordering.md`
