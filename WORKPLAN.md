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
| 1 — Local Event Store (Daemon Side) | ✅ Done | [Data model](docs/06-data-model.md), [local-durability.feature](docs/bdd/features/local-durability.feature) (deferred — see notes), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [Event Recording Flow](docs/sequence-diagrams.md) |
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

## Phase 1 — Local Event Store (Daemon Side) ✅ Done

**Related docs**: [Data model](docs/06-data-model.md) (`EventEnvelope`, `EventRecord`, `HlcGenerator`, idempotent apply shape), [local-durability.feature](docs/bdd/features/local-durability.feature), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [Event Recording Flow diagram](docs/sequence-diagrams.md)

**Entry criteria:** Phase 0 complete. ✅

- [x] Implement `EventEnvelope`, `HybridLogicalClock`, `HlcGenerator` in `SyncMesh.Contracts`
- [x] Local IPC listener: named pipe (`System.IO.Pipes` — cross-platform, no gRPC/Kestrel needed for Phase 1's scope), length-prefixed JSON framing, one request/response per connection, each handled in its own DI scope
- [x] Append-only write path (`LocalEventWriter`): assigns `GlobalEventId` + HLC + next `StreamVersion`, persists via `EventStoreDbContext`
- [x] Optimistic concurrency enforcement via `(StreamId, StreamVersion)` unique index — retry loop on `DbUpdateException`, verified under 10 concurrent writers to the same stream (all get unique sequential versions)
- [x] Buffered read path (`LocalEventReader`): local app reads back what's been recorded this session, ordered by `StreamVersion`, served entirely from the daemon's own local store
- [x] `SyncMesh.Daemon.Tests` (xUnit): HLC generation/merge monotonicity, append/read path, concurrent-write safety, and a restart-survival proof (fresh `EventStoreDbContext` against the same SQLite file sees previously written rows) — 9/9 passing

**Exit criteria:**
- [x] `event-ordering-and-idempotency.feature`: the two scenarios genuinely testable without network — "Clock merge preserves causal ordering" and "Events from two sites are ordered correctly on replay" — pass. The other three (duplicate-delivery/idempotent-apply, partition reconciliation, leaf reconnect) remain correctly pending — they're Phase 2/3 scope (server-side apply, multi-server mesh, leaf node).
- [~] `local-durability.feature`: **deliberately left pending, not step-defined this phase.** Its `Background` asserts "a local daemon is running with an embedded NATS leaf node" and "the daemon's local JetStream stream uses WorkQueue retention" — neither exists until Phase 2. Binding those steps now would mean asserting NATS/JetStream behavior that isn't there, which is worse than leaving them honestly pending. The underlying property this feature is really after — durable local storage that survives a daemon restart — **is proven**, just via `SyncMesh.Daemon.Tests` instead of this Gherkin file (see `WrittenEvent_SurvivesAFreshDbContext_SimulatingADaemonRestart`). Revisit this feature file in Phase 2 once the Background is literally true.

**Feature files reconciled against everything discussed since they were first
written** (buffer floor/ceiling + disk-bound default, buffered local read,
client-isolated/no-nearest-server, standalone server, two-level topology +
full-mesh-to-cloud, TLS + service-credential baseline):
- `local-durability.feature`: split the old single capacity-cap scenario
  into "defaults to disk-bound" + "respects an explicit smaller cap"
  (reject-new-writes, not evict); added scenarios for buffered local read
  and for a daemon with no nearest server configured at all (permanent,
  not just an outage).
- `nearest-neighbor-sync.feature`: added scenarios for cloud-only (no
  on-prem tier), a standalone zero-peer server, intra-site full mesh with
  a limited inter-site gateway, and full mesh extending directly to cloud;
  the existing multi-site reconciliation scenario now states the two-way
  direction explicitly (A applies B's events *and* B applies A's).
- `remote-monitoring-tunnel.feature`: added a scenario for the TLS +
  registered-service-credential baseline, with remote-user authorization
  as an explicit separate layer on top.
- `event-ordering-and-idempotency.feature`: **left untouched** — nothing
  discussed changes its content, and two of its scenarios already have
  passing step bindings from this phase that textual changes would break.

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

**Related docs**: [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (see Amendment), [ADR-0003](docs/adr/0003-hybrid-logical-clock-ordering.md), [Data model §3](docs/06-data-model.md) (`HlcGenerator.Merge`), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Server Mesh Reconciliation diagram](docs/sequence-diagrams.md), [Deployment models](docs/08-deployment-models.md)

**Entry criteria:** Phase 2 complete, at least two server-tier instances available for testing.

Note: a standalone (zero-peer) server is a fully valid, permanent deployment on its own — this phase is about *multi-site* deployments specifically, not something every deployment must eventually adopt.

- [ ] Intra-site NATS gateway connections between a site's own server-tier nodes, TLS-secured with registered service credentials — full mesh *within* a site is the default assumption (reliable LAN connectivity)
- [ ] Inter-site gateway connections (e.g. on-prem ↔ cloud) via a single/limited designated gateway server per site by default — not full mesh across every server at every site, though that remains supported if ever wanted
- [ ] Server-side apply logic merges incoming HLC values — two-way sync, every connected server both publishes and applies, converging to the same fully-replicated history regardless of which physical links carried the data
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

**Related docs**: [ADR-0004](docs/adr/0004-separate-tunnel-from-event-mesh.md) (see Amendment), [remote-monitoring-tunnel.feature](docs/bdd/features/remote-monitoring-tunnel.feature), [Tunnel Fallback diagram](docs/sequence-diagrams.md), [Design doc §8](docs/00-design-document.md) (Open Question 5 — security baseline + phase gating decided, full review moved to Phase 6)

**Entry criteria:** Phase 2 complete. The full security review (Open Question 5) is **out of scope for this phase** — it's a Phase 6 pre-production gate, not a POC/prototype blocker. This phase ships against the security baseline already decided (TLS + registered service credentials), not the full review.

- [ ] Tunnel/relay tooling integrated as a mechanism separate from the NATS event mesh, TLS-secured, authenticating with a registered service credential (not end-user permissions) — remote-user authorization for what they can view/control is a separate layer on top
- [ ] Direct-connection-first, relay-fallback logic on the client side
- [ ] Explicit chaos-style tests: kill tunnel path, confirm event-sync unaffected, and vice versa

**Exit criteria:**
- [ ] `remote-monitoring-tunnel.feature` fully passes, including both cross-failure-isolation scenarios
- [ ] No security-review sign-off required to exit this phase (that gate is in Phase 6) — but this phase's output must not be treated as production-ready regardless

## Phase 6 — Hardening & Operational Readiness

**Related docs**: [Design doc §8](docs/00-design-document.md) (all Open Questions), `docs/adr/` (re-review as needed)

**Entry criteria:** Phases 1–5 functionally complete. This is the
pre-production-readiness phase — nothing here is required for a POC.

- [x] ~~Buffer cap sizing~~ (Open Question 1) — resolved: floor is "until server acks," ceiling defaults to disk-bound, configurable smaller
- [ ] Server-tier retention/backup policy defined (Open Question 3 — see
      `docs/07-operations-guide.md` for the ops-owned/dev-owned split)
- [ ] Full mesh validated/decided default-vs-opt-in given actual site count and instability characteristics (Open Question 4) — standalone and intra-site full mesh already work by this point
- [ ] Load/chaos test leaf-node reconnect behavior under realistic outage durations/volumes (Open Question 2)
- [ ] Complete the dedicated tunnel/relay security review (Open Question 5) and obtain sign-off — required before any production deployment, not before this phase's own completion in a non-production context
- [x] ~~WCF/legacy interop scope~~ (Open Question 6) — resolved: out of scope for this project

**Exit criteria:**
- [ ] All Open Questions in `docs/00-design-document.md` §8 are resolved and documented, or explicitly accepted as ongoing risks with a named owner and review date

---

## Open questions carried from the design doc

Mirrors `docs/00-design-document.md` §8 — flagged, not silently decided, per
`CLAUDE.md`. Checked = actually decided; unchecked = still needs a
product/ops decision, don't resolve it here.

- [x] **1. Buffer cap sizing at the daemon.** Resolved: floor is "never
      discard before the server acks it" (WorkQueue retention); ceiling
      defaults to unbounded except by available local disk, configurable
      to a smaller explicit `MaxBytes`/`MaxAge`/`MaxMsgs` via `IOptions<T>`
      (`ARCHITECTURE.md` → Configuration). Disk-exhaustion behavior is
      reject-new-writes, not evict-unacked-data. See design doc §4.2.
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
        RPO/RTO targets — still open; the smart default isn't that
        sign-off. **Out of scope for POC** — a Phase 6 pre-release gate,
        like Open Question 5.
- **4. Full-mesh vs. hub-and-spoke topology at scale** (Phase 6).
  - [x] Policy decided — topology is fully flexible and config-driven, no
        architectural minimum or maximum on server/site/gateway count.
        Common patterns: full mesh **within** a site, with a limited
        designated gateway per site for **cross-site** links — but full
        mesh extending to cloud/remote sites directly is equally valid, and
        no on-prem tier is required at all (a daemon can connect straight
        to a cloud server). None of these are mutually exclusive or
        privileged. Every server everywhere still converges to the same
        fully-replicated history regardless of which pattern is used. See
        `docs/08-deployment-models.md` for diagrams.
  - [x] Standalone (a single server, permanently, zero live peer
        connections, no minimum node count) — including a daemon with no
        nearest server at all ("client isolated") — is a first-class
        deployment mode in its own right, not a bootstrapping step toward a
        mesh. Later reconciliation may be offline/batch rather than a live
        gateway — compatible with idempotent apply/HLC ordering without
        redesign.
  - [ ] How many designated gateway servers per inter-site link (one vs. a
        small redundant set for HA), and which pattern to actually use for
        a real deployment — still open, revisit once site count/
        instability is known. **Out of scope for POC** — a Phase 6
        pre-release gate.
  - [ ] The offline/batch sync mechanism itself for a standalone site —
        undesigned, a distinct future decision.
- **5. Tunnel relay security model.**
  - [x] Security baseline decided — TLS-secured, authenticating with
        registered service credentials scoped to the daemon/server
        instance, never end-user permissions (same as the event mesh; see
        `docs/adr/0002-nats-leaf-nodes-for-transport.md` Amendment and
        `docs/adr/0004-separate-tunnel-from-event-mesh.md` Amendment).
  - [x] Phase gating decided: the full review is a **Phase 6
        pre-production readiness gate**, not a POC/prototype blocker for
        Phase 5.
  - [ ] Full dedicated security review itself still required before
        production: attack surface, the remote-user authorization layer on
        top of the baseline above, session hijacking risk, etc.
- [x] **6. WCF/legacy interop boundary scope** — resolved: out of scope for
      this project. Any future external component needing WCF integration
      implements it within that component (anti-corruption layer), not in
      sync-mesh.
