# Implementation Guide

Phased plan intended for execution by Claude Code, one phase at a time.
Each phase lists entry criteria, scope, exit criteria, and the BDD feature
files that should pass before moving to the next phase. Do not skip ahead —
later phases assume earlier ones are solid, especially around idempotency
and HLC ordering.

## Phase 0 — Project Setup

**Entry criteria:** none (starting point).

**Scope:**
- Solution structure: separate projects for `Daemon`, `ServerHost`,
  `EventStore` (shared EF Core library), `Contracts` (shared envelope/DTOs),
  and a `Tests` project (unit + BDD step definitions).
- Wire up EF Core with a provider-agnostic `DbContext` (see
  `docs/06-data-model.md`), configurable at startup for SQLite (daemon) or
  PostgreSQL/SQL Server (server).
- Add NuGet references: EF Core (+ Sqlite/Npgsql/SqlServer providers), a
  NATS .NET client, a BDD test runner (e.g. Reqnroll/SpecFlow-successor or
  equivalent current tooling — verify current recommended package before
  committing, as this space has changed over time).

**Exit criteria:**
- Solution builds; `EventStoreDbContext` can migrate against all three
  providers in isolated test projects.
- CI (or local script) runs unit tests + BDD scenarios (empty/pending is
  fine at this stage).

## Phase 1 — Local Event Store (Daemon Side)

**Entry criteria:** Phase 0 complete.

**Scope:**
- Implement `EventEnvelope`, `EventRecord`, `HlcGenerator` per
  `docs/06-data-model.md`.
- Implement local IPC listener (named pipe / gRPC) accepting events from a
  stub "local app" client.
- Implement append-only write path: assign `GlobalEventId` + HLC, persist
  to local SQLite via EF Core.
- Implement optimistic concurrency enforcement via the
  `(StreamId, StreamVersion)` unique index.

**Exit criteria:**
- `docs/bdd/features/local-durability.feature` scenarios pass against a
  local-only setup (no server tier yet) for "event is durably stored"
  behavior.
- `docs/bdd/features/event-ordering-and-idempotency.feature` scenarios for
  HLC generation/merge pass in isolation (no network yet).

## Phase 2 — Local Daemon ↔ Nearest Server (NATS Leaf Node)

**Entry criteria:** Phase 1 complete.

**Scope:**
- Stand up a local nats-server instance (or embedded equivalent) configured
  as a leaf node dialing out to a nearest-server nats-server cluster.
- Configure local JetStream stream with WorkQueue retention and a
  conservative `MaxAge`/`MaxMsgs` cap (values TBD — flag as configurable,
  not hardcoded; see Open Question 1 in the design doc).
- Implement publish-on-write from the daemon's event writer to the local
  JetStream stream.
- Implement a minimal server-side subscriber that acknowledges receipt and
  writes to a server-tier `EventStoreDbContext` (PostgreSQL or SQL Server).
- Implement idempotent apply (dedupe by `GlobalEventId`) on the server
  side.

**Exit criteria:**
- `docs/bdd/features/local-durability.feature`: full feature passes,
  including buffer removal after ack and cap-overflow behavior.
- `docs/bdd/features/nearest-neighbor-sync.feature`: on-prem connection
  scenario passes; config-swap scenario passes by changing connection
  profile only.
- Explicit test: simulate an extended disconnect/reconnect and verify
  event delivery — this directly tests the known leaf-node reconnect-sync
  risk flagged in ADR-0002. Do not consider this phase done until this test
  exists and passes, or the risk is documented as an accepted limitation
  with a mitigation plan.

## Phase 3 — Server Mesh Reconciliation (Gateways/Supercluster)

**Entry criteria:** Phase 2 complete, with at least two server-tier
instances available for testing.

**Scope:**
- Configure NATS gateway connections between two or more server-tier nodes
  (start with a simple two-node hub-and-spoke shape, not full mesh).
- Extend the server-side apply logic to merge incoming HLC values (see
  `HlcGenerator.Merge`) and confirm ordering is reconstructed correctly on
  replay across sites.
- Implement a replay/read-model query that orders by
  `(HlcPhysicalTicks, HlcLogicalCounter)`, not by insertion order or
  arrival time.

**Exit criteria:**
- `docs/bdd/features/event-ordering-and-idempotency.feature`: full feature
  passes, including out-of-order arrival and partition/reconnect scenarios.
- `docs/bdd/features/nearest-neighbor-sync.feature`: server mesh
  reconciliation scenario passes across at least two server nodes.

## Phase 4 — Passive Monitoring

**Entry criteria:** Phase 2 complete (does not require Phase 3).

**Scope:**
- Publish daemon telemetry/status to `monitor.<siteId>.<instanceId>.*`
  subjects on the same NATS mesh.
- Implement a minimal remote client (or CLI) subscribing to monitor
  subjects for a given site/instance.

**Exit criteria:**
- `docs/bdd/features/remote-monitoring-tunnel.feature`: passive monitoring
  scenario passes.

## Phase 5 — Interactive Tunnel + Relay Fallback

**Entry criteria:** Phase 2 complete. The dedicated security review (Open
Question 5) is **out of scope for this phase** — it's a pre-production
gate (Phase 6), not a POC/prototype blocker. Phase 5 ships against the
security *baseline* already decided (TLS, registered service credentials —
see ADR-0002/ADR-0004 Amendments), not the full review.

**Scope:**
- Integrate chosen tunnel/relay tooling (e.g. `frp`/`chisel` or overlay
  networking) as a mechanism separate from the NATS event mesh.
- Implement direct-connection-first, relay-fallback logic on the client
  side.
- Ensure the tunnel/relay path has no dependency on event-mesh health and
  vice versa (verify with explicit chaos-style tests: kill one, confirm the
  other is unaffected).

**Exit criteria:**
- `docs/bdd/features/remote-monitoring-tunnel.feature`: full feature
  passes, including both cross-failure-isolation scenarios.
- No security-review sign-off required to exit this phase — that gate
  lives in Phase 6. Phase 5's own POC/prototype must not be treated as
  production-ready regardless.

## Phase 6 — Hardening & Operational Readiness

**Entry criteria:** Phases 1–5 functionally complete. This is the
pre-production-readiness phase — nothing here is required for a POC.

**Scope:**
- Capacity-plan and finalize local buffer caps (Open Question 1).
- Define and implement server-tier retention/backup policy (Open
  Question 3).
- Decide and document full-mesh vs. hub-and-spoke topology given actual
  site count (Open Question 4).
- Load/chaos test leaf-node reconnect behavior under realistic outage
  durations and event volumes (Open Question 2), not just the Phase 2
  smoke test.
- Complete the dedicated tunnel/relay security review (Open Question 5)
  and obtain sign-off — required before any production deployment, not
  before this phase's own completion in a non-production context.
- ~~Confirm WCF/legacy interop scope~~ — resolved (Open Question 6): out of
  scope for this project. No Phase 6 work item here.

**Exit criteria:**
- All Open Questions in `docs/00-design-document.md` §8 are either
  resolved and documented, or explicitly accepted as ongoing risks with a
  named owner and review date.

## General Working Agreements for Claude Code

- Treat every `docs/bdd/features/*.feature` scenario as an acceptance test
  to implement against — write/step-define them before or alongside the
  implementation, not after.
- Any new architectural decision made during implementation that isn't
  already covered by an ADR should get a new ADR using
  `docs/templates/adr-template.md`.
- Do not silently resolve any item under "Open Questions & Risks" in the
  design document — surface it for a decision.
- Keep provider-specific code (SQLite vs. PostgreSQL vs. SQL Server)
  isolated to DI/configuration; the application and domain layers should
  have zero knowledge of which provider is active.
