# Work Plan

Living tracker for implementation progress against `docs/05-implementation-guide.md`.
That document is the authoritative phase definition (entry/exit criteria, BDD
features per phase) — this file tracks *status*: what's done, what's
in-flight, what's next. Update it as work progresses; do not let it drift
from reality.

For engineering patterns/practices/conventions adopted along the way (not
phase status), see `ARCHITECTURE.md` instead.

## Status at a glance

| Phase | Status | Related docs |
|---|---|---|
| 0 — Project Setup | ✅ Done | [Data model](docs/06-data-model.md), [ADR-0001](docs/adr/0001-event-store-on-ef-core.md) |
| 1 — Local Event Store (Daemon Side) | ⬜ Not started | [Data model](docs/06-data-model.md), [local-durability.feature](docs/bdd/features/local-durability.feature), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [Event Recording Flow](docs/sequence-diagrams.md) |
| 2 — Local Daemon ↔ Nearest Server (NATS Leaf Node) | ⬜ Not started | [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md), [local-durability.feature](docs/bdd/features/local-durability.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Design doc §8](docs/00-design-document.md) (Open Questions 1 & 2) |
| 3 — Server Mesh Reconciliation (Gateways/Supercluster) | ⬜ Not started | [ADR-0003](docs/adr/0003-hybrid-logical-clock-ordering.md), [Data model §3](docs/06-data-model.md), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Server Mesh Reconciliation diagram](docs/sequence-diagrams.md) |
| 4 — Passive Monitoring | ⬜ Not started | [Data model §5](docs/06-data-model.md) (NATS subject naming), [remote-monitoring-tunnel.feature](docs/bdd/features/remote-monitoring-tunnel.feature) |
| 5 — Interactive Tunnel + Relay Fallback | ⬜ Not started | [ADR-0004](docs/adr/0004-separate-tunnel-from-event-mesh.md), [remote-monitoring-tunnel.feature](docs/bdd/features/remote-monitoring-tunnel.feature), [Tunnel Fallback diagram](docs/sequence-diagrams.md), [Design doc §8](docs/00-design-document.md) (Open Question 5 — security review) |
| 6 — Hardening & Operational Readiness | ⬜ Not started | [Design doc §8](docs/00-design-document.md) (all Open Questions), `docs/adr/` (re-review as needed) |

---

## Phase 0 — Project Setup

**Related docs**: [Data model](docs/06-data-model.md) (`EventStoreDbContext` shape), [ADR-0001](docs/adr/0001-event-store-on-ef-core.md) (EF Core on SQLite/PostgreSQL/SQL Server)

- [x] Solution scaffolded (`SyncMesh.slnx`) with `src/` and `tests/` projects
- [x] `SyncMesh.Contracts` — shared envelope/DTO project shell (content deferred to Phase 1)
- [x] `SyncMesh.EventStore` — `EventRecord` entity + provider-agnostic `EventStoreDbContext`
- [x] Per-provider migrations projects (`EventStore.Migrations.{Sqlite,Postgres,SqlServer}`), each with its own `MigrationsAssembly` and design-time factory
- [x] Initial EF Core migration generated for all three providers
- [x] `SyncMesh.Daemon` / `SyncMesh.ServerHost` worker hosts wired to `AddSqliteEventStore` / `AddPostgresEventStore` + `AddSqlServerEventStore` (config-selected)
- [x] `SyncMesh.AppHost` (Aspire) orchestrating `ServerHost` + Postgres container + `Daemon` for local dev
- [x] `SyncMesh.ServiceDefaults` (Aspire) wired into both hosts
- [x] Isolated migration test projects per provider (SQLite in-process, Postgres/SQL Server via Testcontainers) — all passing
- [x] `SyncMesh.Bdd.Tests` (Reqnroll + MSTest) linking `docs/bdd/features/*.feature` as the source of truth
- [x] `dotnet-ef` pinned as a local tool (`.config/dotnet-tools.json`), not global
- [x] Final full-solution `dotnet build` + `dotnet test` pass — 0 failures
      (6 passing provider-migration tests, 18 BDD scenarios correctly
      reported as Skipped/pending)

**Exit criteria (from implementation guide):**
- [x] Solution builds
- [x] `EventStoreDbContext` migrates against all three providers in isolated test projects
- [x] Local script runs unit tests + BDD scenarios (BDD scenarios are pending/skipped — expected, no step definitions exist yet)

## Phase 1 — Local Event Store (Daemon Side)

**Related docs**: [Data model](docs/06-data-model.md) (`EventEnvelope`, `EventRecord`, `HlcGenerator`, idempotent apply shape), [local-durability.feature](docs/bdd/features/local-durability.feature), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [Event Recording Flow diagram](docs/sequence-diagrams.md)

**Entry criteria:** Phase 0 complete. ✅

- [ ] Implement `EventEnvelope`, `EventRecord`, `HlcGenerator` in `SyncMesh.Contracts`
- [ ] Local IPC listener (named pipe / gRPC) accepting events from a stub local-app client
- [ ] Append-only write path: assign `GlobalEventId` + HLC, persist to local SQLite via EF Core
- [ ] Optimistic concurrency enforcement via `(StreamId, StreamVersion)` unique index
- [ ] Buffered read path: local app can read back what it's already recorded this session, served from the daemon's own local store only (never proxied to/from the server — see design doc §4.1/§4.2)

**Exit criteria:**
- [ ] `local-durability.feature` scenarios pass (local-only, no server tier)
- [ ] `event-ordering-and-idempotency.feature` HLC generation/merge scenarios pass in isolation (no network)

## Phase 2 — Local Daemon ↔ Nearest Server (NATS Leaf Node)

**Related docs**: [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (see Amendment), [local-durability.feature](docs/bdd/features/local-durability.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Design doc §8](docs/00-design-document.md) (Open Question 2 — leaf reconnect-sync risk; Open Question 1 resolved)

**Entry criteria:** Phase 1 complete.

- [ ] Local nats-server instance (or embedded equivalent) configured as a leaf node, TLS-secured, authenticating with a registered service credential (not end-user identity)
- [ ] Local JetStream stream, WorkQueue retention: default ceiling unbounded except by local disk (`Discard: New` on exhaustion), configurable to a smaller `MaxBytes`/`MaxAge`/`MaxMsgs` via `IOptions<T>`
- [ ] Publish-on-write from daemon's event writer to local JetStream stream — one-way (daemon → server); no subscription/mirroring of server-side data back down to the daemon
- [ ] Minimal server-side subscriber: ack + write to server-tier `EventStoreDbContext`
- [ ] Idempotent apply (dedupe by `GlobalEventId`) on the server side
- [ ] NATS added to `SyncMesh.AppHost` topology (leaf node ↔ nearest-server cluster)

**Exit criteria:**
- [ ] `local-durability.feature` fully passes, including buffer removal after ack and cap-overflow behavior
- [ ] `nearest-neighbor-sync.feature` on-prem connection + config-swap scenarios pass
- [ ] Explicit extended-disconnect/reconnect test exists and passes, or the leaf-node reconnect-sync risk is documented as an accepted limitation with a mitigation plan

## Phase 3 — Server Mesh Reconciliation (Gateways/Supercluster)

**Related docs**: [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (see Amendment), [ADR-0003](docs/adr/0003-hybrid-logical-clock-ordering.md), [Data model §3](docs/06-data-model.md) (`HlcGenerator.Merge`), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Server Mesh Reconciliation diagram](docs/sequence-diagrams.md)

**Entry criteria:** Phase 2 complete, at least two server-tier instances available for testing.

Note: a standalone (zero-peer) server is a fully valid, permanent deployment on its own — this phase is about *multi-site* deployments specifically, not something every deployment must eventually adopt.

- [ ] NATS gateway connections between two+ server-tier nodes, TLS-secured with registered service credentials — validate hub-and-spoke first; full mesh must remain supported by the topology/config, not architecturally precluded
- [ ] Server-side apply logic merges incoming HLC values
- [ ] Replay/read-model query orders by `(HlcPhysicalTicks, HlcLogicalCounter)`, not insertion/arrival order
- [ ] (Future, undesigned) offline/batch reconciliation mechanism for a standalone site that later needs to sync out-of-band — tracked as a distinct decision, not assumed to be "just NATS gateways later"

**Exit criteria:**
- [ ] `event-ordering-and-idempotency.feature` fully passes, including out-of-order arrival and partition/reconnect scenarios
- [ ] `nearest-neighbor-sync.feature` server-mesh reconciliation scenario passes across two+ nodes

## Phase 4 — Passive Monitoring

**Related docs**: [Data model §5](docs/06-data-model.md) (NATS subject naming), [remote-monitoring-tunnel.feature](docs/bdd/features/remote-monitoring-tunnel.feature)

**Entry criteria:** Phase 2 complete (does not require Phase 3).

- [ ] Daemon telemetry/status published to `monitor.<siteId>.<instanceId>.*`
- [ ] Minimal remote client/CLI subscribing to monitor subjects for a given site/instance

**Exit criteria:**
- [ ] `remote-monitoring-tunnel.feature` passive-monitoring scenario passes

## Phase 5 — Interactive Tunnel + Relay Fallback

**Related docs**: [ADR-0004](docs/adr/0004-separate-tunnel-from-event-mesh.md) (see Amendment), [remote-monitoring-tunnel.feature](docs/bdd/features/remote-monitoring-tunnel.feature), [Tunnel Fallback diagram](docs/sequence-diagrams.md), [Design doc §8](docs/00-design-document.md) (Open Question 5 — security baseline decided, full review still required)

**Entry criteria:** Phase 2 complete. Full security review (Open Question 5) must be scheduled/completed before production use, even if a prototype is built earlier — the TLS + service-credential baseline below isn't a substitute for that review.

- [ ] Tunnel/relay tooling integrated as a mechanism separate from the NATS event mesh, TLS-secured, authenticating with a registered service credential (not end-user permissions) — remote-user authorization for what they can view/control is a separate layer on top
- [ ] Direct-connection-first, relay-fallback logic on the client side
- [ ] Explicit chaos-style tests: kill tunnel path, confirm event-sync unaffected, and vice versa

**Exit criteria:**
- [ ] `remote-monitoring-tunnel.feature` fully passes, including both cross-failure-isolation scenarios
- [ ] Security review sign-off obtained, or explicitly deferred with risk accepted by a named owner

## Phase 6 — Hardening & Operational Readiness

**Related docs**: [Design doc §8](docs/00-design-document.md) (all Open Questions), `docs/adr/` (re-review as needed)

**Entry criteria:** Phases 1–5 functionally complete.

- [ ] Buffer cap sizing finalized (Open Question 1)
- [ ] Server-tier retention/backup policy defined (Open Question 3 — see
      `docs/07-operations-guide.md` for the ops-owned/dev-owned split)
- [ ] Full mesh validated/decided default-vs-opt-in given actual site count and instability characteristics (Open Question 4) — standalone and hub-and-spoke already work by this point
- [ ] Load/chaos test leaf-node reconnect behavior under realistic outage durations/volumes (Open Question 2)
- [x] ~~WCF/legacy interop scope~~ (Open Question 6) — resolved: out of scope for this project

**Exit criteria:**
- [ ] All Open Questions in `docs/00-design-document.md` §8 are resolved and documented, or explicitly accepted as ongoing risks with a named owner and review date

---

## Open questions carried from the design doc

Mirrors `docs/00-design-document.md` §8 — flagged, not silently decided, per
`CLAUDE.md`. Checked = actually decided; unchecked = still needs a
product/ops decision, don't resolve it here.

- [ ] **1. Buffer cap sizing at the daemon** (Phase 6). Will ship as an
      `IOptions<T>` class (e.g. `DaemonBufferOptions` with
      `MaxAge`/`MaxMsgs`) per `ARCHITECTURE.md` → Configuration; default
      value still needs a decision once expected outage duration is known.
- [ ] **2. Leaf node reconnect-sync reliability** (Phase 2 exit criteria
      requires an explicit test; Phase 6 requires load/chaos testing).
      Reconnect/backoff settings will also be `IOptions<T>`-bound with a
      smart default.
- **3. Server-tier retention/backup policy** (Phase 6).
  - [x] Ownership split decided — see
        [`docs/07-operations-guide.md`](docs/07-operations-guide.md):
        backup/restore mechanics are ops-owned; only purge-safety
        (idempotent-apply/replay-ordering) is dev-owned.
  - [x] Smart default established (healthcare/clinical-adjacent data): 7
        years for adult records, a longer distinct default for minors (age
        of majority + additional years) — see
        [`docs/07-operations-guide.md`](docs/07-operations-guide.md) →
        "Retention default". `IOptions<T>`-bound per `ARCHITECTURE.md`.
  - [ ] Compliance/legal sign-off on the exact figures for the actual
        jurisdiction(s)/accreditation this deployment operates under, plus
        RPO/RTO targets — still open; the smart default isn't that sign-off.
- **4. Full-mesh vs. hub-and-spoke topology at scale** (Phase 6).
  - [x] Policy decided — full mesh must remain a supported gateway topology
        (not architecturally precluded); standalone (single server) and
        hub-and-spoke are the minimum-scale configurations validated first.
  - [ ] Which topology to actually default to at real scale — still open,
        revisit once node count/instability characteristics are known.
- [ ] **5. Tunnel relay security model** — needs dedicated security review
      before Phase 5 is production-ready.
- [x] **6. WCF/legacy interop boundary scope** — resolved: out of scope for
      this project. Any future external component needing WCF integration
      implements it within that component (anti-corruption layer), not in
      sync-mesh.
