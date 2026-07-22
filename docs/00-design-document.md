# Design Document: Distributed Event-Sourced Recording & Sync Mesh

| | |
|---|---|
| Status | Draft — ready for implementation planning |
| Owners | (assign) |
| Last updated | 2026-07-22 |
| Related | `docs/adr/`, `docs/05-implementation-guide.md`, `docs/bdd/features/` |

## 1. Purpose

Enable a local recording application to durably capture events during a
recording session, hand those events off to a nearby server with minimal
latency, and have the full server mesh (on-prem, WAN, cloud) converge on a
single, correctly ordered event history — while also supporting remote
monitoring of an active recording instance, tunneling through the nearest
server when direct access is blocked.

## 2. Goals

- Local app never talks directly to anything beyond its own daemon.
- Daemon-to-server communication survives NAT/firewalls without inbound
  port configuration, and works whether the "nearest server" is on-prem,
  reached over WAN, or in the cloud — selectable by configuration, not code.
- Local daemon storage is durable **only for the duration of a recording
  session** — it is a handoff buffer, not a system of record.
- Servers reconcile events from all sites into one consistent, ordered
  history, favoring fast + reliable delivery, with ordering reconstructed
  deterministically rather than relying on transport guarantees.
- A remote client can observe or interactively reach a recording instance
  directly; if that's blocked, it falls back to relaying through the
  nearest server.

## 3. Non-goals

- This is not a general-purpose message bus for unrelated application
  traffic — scope the mesh to recording events + monitoring/tunnel traffic.
- Not attempting synchronous/strong consistency across sites. The system is
  eventually consistent by design; do not introduce distributed
  transactions across the mesh.
- Not replacing existing enterprise messaging where one already exists —
  this design assumes we are introducing the transport, not retrofitting
  onto a mandated existing broker (if one is mandated, see ADR-0002 for the
  fallback discussion).

## 4. Architecture Overview

Four tiers, each with a distinct durability and connectivity contract.

```
Tier 0  Local App          same-machine IPC only, no durability of its own
   |
Tier 1  Local Daemon        durable ONLY while recording (short-lived buffer)
   |    (leaf node, outbound-only connection)
Tier 2  Nearest Server      on-prem / WAN / cloud — config-selected
   |    (gateway / supercluster connections, peer-to-peer or peer-to-cloud)
Tier 3  Server Mesh         durable system of record, HLC-ordered replay

Tier X  Remote Monitoring   direct to recording instance, else relay via
                            nearest server (separate concern from Tier 1-3)
```

See `docs/c4-diagrams.md` for the formal C4 Context and Container diagrams,
and `docs/sequence-diagrams.md` for the recording, sync, and tunnel-fallback
flows (PlantUML source embedded inline in each Markdown file).

### 4.1 Tier 0 — Local App ↔ Local Daemon

- Communication: named pipe / Unix domain socket / local gRPC (pick one per
  platform; do not use network sockets for same-machine IPC).
- No durability requirement at this hop — the daemon owns durability from
  the moment it accepts an event.
- Keep the local app dependency-free with respect to the sync mesh; it
  should never need reconfiguration when the daemon's upstream topology
  changes.
- The local app can also read back what it has already recorded this
  session — a **buffered read**, served entirely from the daemon's own
  local store. The daemon never proxies reads to/from the server to satisfy
  this: it only ever holds what the local app itself is writing (or has
  recently written), never a mirror of server-side history. See §4.2.

### 4.2 Tier 1 — Local Daemon ↔ Nearest Server

- Transport: NATS leaf node. The daemon runs (or embeds) a NATS server
  configured as a leaf node dialing out to the nearest server's cluster.
- **Sync is one-way**: daemon → server, for writes only. The server never
  pushes a mirror/replica of its dataset back down to the daemon — the
  daemon has no need for "everything from the server," only what the local
  user is actively writing or reading back locally (§4.1). Nothing in the
  leaf-node topology should be used to sync data downstream.
- Durability: local JetStream stream with **WorkQueue retention**.
  Retention has a floor and a (configurable) ceiling, not a single fixed
  cap:
  - **Minimum**: at least until the nearest server acknowledges receipt —
    never discard a locally-buffered event before it's confirmed upstream.
    This is exactly what WorkQueue retention gives you: the local copy is
    removed once consumed/acked, not before.
  - **Maximum**: configurable (`IOptions<T>`, per `ARCHITECTURE.md` →
    Configuration), defaulting to **unbounded except by available local
    disk** — i.e. try to keep everything until the disk actually runs out,
    rather than guessing at an outage duration up front. Ops can configure
    a smaller explicit `MaxBytes`/`MaxAge`/`MaxMsgs` ceiling per deployment
    if a bounded footprint is wanted instead.
- Rationale for "durable only while recording": recording sessions are
  bounded in time; once a session ends and its events are confirmed
  received upstream, there is no requirement to retain them locally.
- Failure mode: if the nearest server is unreachable, the leaf node queues
  locally, growing up to whatever ceiling is configured (disk space by
  default), and flushes automatically on reconnect. If local disk actually
  fills before the server becomes reachable again, the safe behavior is to
  **reject new local writes** (JetStream `Discard: New`, with the rejection
  surfaced back to the local app) rather than evict any unacknowledged
  event — evicting unacked data would violate the minimum-retention
  guarantee above. This is not a business/product open question: it's the
  only behavior consistent with the durability guarantee, so it's decided,
  not left open.

### 4.3 Tier 2 — "Nearest Server" Selection

- On-prem, WAN-connected, and cloud servers are all just NATS clusters from
  the daemon's point of view; which one is "nearest" is a configuration
  value (connection URL + credentials), not a code branch.
- This reuses the transport-abstraction goal discussed earlier in the
  project: swapping environments is a config change.

### 4.4 Tier 3 — Server Mesh Reconciliation

- Servers connect to each other via NATS gateway connections (supercluster)
  for peer-to-peer, and/or peer-to-cloud where a cloud region acts as a
  hub. All server↔server (and daemon↔server) connections use TLS and
  authenticate with **registered service credentials scoped to the
  daemon/server instance** — never end-user identity/permissions. See
  §4.5 and ADR-0002 for the same requirement applied to the tunnel path.
- Full mesh must remain a supported gateway topology — nothing in the
  design should architecturally foreclose it. Hub-and-spoke is the default
  starting shape for multi-site deployments; full mesh is the thing to
  validate once real node count/instability characteristics are known (see
  ADR-0002 and Open Question 4).
- **Standalone is a first-class, permanent topology in its own right — not
  merely a minimal starting point that's expected to eventually join a
  mesh.** A single server with no peer connections at all must work
  correctly, indefinitely, on its own. Some deployments may only ever
  reconcile with other sites later, out-of-band (e.g. a batch/offline
  export-import rather than a live NATS gateway connection). That works
  without redesigning the reconciliation logic, because idempotent apply
  and HLC ordering (below, ADR-0003) don't depend on *how* an event
  arrives — only on its `GlobalEventId` + HLC being present. The specific
  offline/batch sync mechanism itself (for a standalone site that later
  needs it) is not yet designed — treat it as a distinct future decision,
  not an assumed extension of live gateway topology.
- Topology is a gateway/config concern, not a code branch — the
  reconciliation logic (HLC ordering, idempotent apply) must not assume any
  particular shape, live or offline.
- Ordering is **not** guaranteed by the transport across sites. Every event
  carries a Hybrid Logical Clock (HLC) + origin site ID. The authoritative
  order is reconstructed at replay time, not assumed from arrival order.
  See ADR-0003 and `docs/06-data-model.md`.
- Every consumer must be idempotent: dedupe by a global event ID
  (`(OriginSiteId, StreamId, StreamVersion)` or a GUID) before applying.

### 4.5 Tier X — Remote Monitoring & Tunnel

- Telemetry/status: published as ordinary subjects on the same NATS mesh
  (e.g. `monitor.<siteId>.<instanceId>.*`). No separate infrastructure
  needed; existing interest-graph routing carries it through leaf/gateway
  connections.
- Interactive tunnel (remote desktop / raw TCP / live control): a
  **separate** mechanism from the event mesh — a reverse-tunnel/relay tool
  (e.g. self-hosted `frp`/`chisel`, or an overlay network such as
  WireGuard/Tailscale-style tooling). Attempt direct connectivity first;
  fall back to relaying through the nearest server when firewalls block
  direct access.
- **Security baseline (decided, ahead of the full review in Open Question
  5)**: the tunnel/relay path must be TLS-secured, and — like the rest of
  the mesh (§4.4) — authenticates with **registered service credentials**,
  not end-user permissions. A remote user's own identity/authorization for
  *what they're allowed to view or control* is a separate, additional
  layer on top of this — the transport-level connection itself never rides
  on end-user credentials. This doesn't replace the dedicated security
  review still required before production (attack surface, session
  hijacking risk, the remote-user authorization layer itself) — it's the
  baseline that review builds on.
- Keep this tier's failure domain isolated from event sync — a monitoring
  outage must never affect recording durability or event delivery, and
  vice versa.

## 5. Data Model (summary — full detail in `docs/06-data-model.md`)

Every event carries, at minimum:
- `GlobalEventId` (GUID) — for idempotent apply / dedupe.
- `StreamId`, `StreamVersion` — aggregate identity and local ordering.
- `OriginSiteId` — which daemon/server first recorded the event.
- `HlcTimestamp` — hybrid logical clock value for cross-site ordering.
- `RecordedAtUtc` — wall-clock time, informational only, never used for
  authoritative ordering.
- `EventType`, `PayloadJson` (or equivalent).

## 6. Non-Functional Requirements

| Concern | Requirement |
|---|---|
| Delivery | At-least-once, everywhere. Exactly-once is not assumed anywhere in the design. |
| Ordering | Deterministic on replay via HLC, not relied upon from transport. |
| Local durability | Bounded to the active recording session; not a permanent store. |
| Server durability | Full system of record; standard backup/retention policy applies (see Open Questions). |
| Connectivity | Daemon and server always dial out; no inbound firewall rules required for the sync path. |
| Config-driven environment swap | On-prem / WAN / cloud selection is configuration, not code. |
| Failure isolation | Monitoring/tunnel failures must not affect event sync, and vice versa. |

## 7. Key Decisions (see `docs/adr/` for full rationale)

- **ADR-0001**: Local/server event store built on EF Core, portable across
  SQLite (edge) and PostgreSQL/SQL Server (server tier).
- **ADR-0002**: NATS (Core + JetStream) with leaf nodes and gateway/
  supercluster connections as the transport across all tiers.
- **ADR-0003**: Hybrid Logical Clocks for cross-site event ordering instead
  of relying on transport-level ordering.
- **ADR-0004**: Remote monitoring/tunnel kept architecturally separate from
  the event-sync mesh, sharing only the "relay via nearest server" fallback
  concept.

## 8. Open Questions & Risks

These require a product or operational decision, not just an engineering
one — flag back to the human rather than resolving silently. Checked boxes
mark what has actually been decided; unchecked items (or unchecked
sub-items) are still open and should not be resolved without asking.

- [x] **1. Buffer cap sizing at the daemon.** Resolved: retention has a
      floor and a configurable ceiling, not a single guessed number (see
      §4.2). Minimum is until the nearest server acks receipt (never
      discard unacknowledged data). Maximum defaults to unbounded except by
      available local disk — store everything until the disk actually runs
      out, rather than pre-guessing an outage duration — and is
      configurable to a smaller explicit cap via `IOptions<T>` (see
      `ARCHITECTURE.md` → Configuration). If local disk fills before the
      server is reachable again, new local writes are rejected
      (`Discard: New`) rather than evicting unacknowledged data.
- [ ] **2. Leaf node reconnect-sync reliability.** There are known reports
      of gaps in leaf-node mirror sync after extended disconnection. This
      needs explicit integration testing against your actual outage
      patterns before being trusted for correctness-critical data — do not
      assume it "just works" from documentation alone.
- **3. Server-tier retention/backup policy.**
  - [x] Ownership split decided: backup/restore mechanics are ops-owned —
        standard, transparent tooling per provider (see
        `docs/07-operations-guide.md`). Development/design only owns
        purge-safety: whether purging (not just backing up) old rows is
        ever safe without breaking idempotent-apply or replay-ordering
        guarantees.
  - [x] Smart default established (healthcare/clinical-adjacent data,
        common U.S. industry practice — see `docs/07-operations-guide.md`
        → "Retention default"): 7 years for adult records; a longer,
        distinct default for minors (age of majority + additional years).
        Ships as an `IOptions<T>` default per `ARCHITECTURE.md` →
        Configuration, not a hardcoded constant.
  - [ ] Sign-off from a named compliance/legal owner on the exact figures
        (both adult and minor) for the actual jurisdiction(s)/accreditation
        requirements this deployment operates under, plus RPO/RTO targets,
        is still open. A smart default is a starting point, not that
        sign-off.
- **4. Full-mesh vs hub-and-spoke topology at scale.**
  - [x] Policy decided: full mesh must remain a supported gateway
        topology — nothing in the design forecloses it. Hub-and-spoke is
        the default starting shape for multi-site deployments.
  - [x] Standalone (a single server, permanently, with no live peer
        connections at all) is a first-class, valid deployment mode in its
        own right — not a bootstrapping step toward a mesh. There is no
        minimum node count. Any later reconciliation for such a site may
        be offline/batch rather than a live gateway connection; this is
        compatible with idempotent apply/HLC ordering without redesign
        (see §4.4).
  - [ ] Which topology (hub-and-spoke vs. full mesh) to actually default to
        for *multi-site* deployments at real scale is still open — revisit
        once the number of server-tier sites and their instability
        characteristics are known.
  - [ ] The offline/batch sync mechanism itself, for a standalone site that
        later needs to reconcile with others out-of-band, is undesigned —
        a distinct future decision, not assumed to just be "NATS gateways,
        later."
- **5. Tunnel relay security model.**
  - [x] Security baseline decided (see §4.5): TLS-secured, authenticating
        with registered service credentials scoped to the daemon/server
        instance — never end-user permissions. A remote user's own
        authorization for what they're allowed to view/control is a
        separate layer on top of this transport-level baseline.
  - [ ] The full dedicated security review is still required before
        production: attack surface, the remote-user authorization layer
        itself, session hijacking risk, etc. This doc only covers the
        architectural shape — the baseline above doesn't substitute for
        that review.
- [x] **6. WCF/legacy interop boundary.** Resolved: out of scope for this
      project. If a specific external component ever needs WCF integration
      with an older on-prem system, that's implemented within that
      component — isolated behind an anti-corruption layer (see
      `CLAUDE.md`) — not built into sync-mesh's core scope.
