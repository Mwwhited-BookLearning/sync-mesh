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
| 2 — Local Daemon ↔ Nearest Server (NATS Leaf Node) | ✅ Done | [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (2026-07-23 Amendment), [local-durability.feature](docs/bdd/features/local-durability.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Design doc §8](docs/00-design-document.md) (Open Questions 1 & 2 — both resolved) |
| 3 — Server Mesh Reconciliation (Gateways/Supercluster) | ✅ Done | [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (2026-07-23 Phase 3 Amendment), [ADR-0003](docs/adr/0003-hybrid-logical-clock-ordering.md), [Data model §3](docs/06-data-model.md), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Server Mesh Reconciliation diagram](docs/sequence-diagrams.md) |
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

## Phase 2 — Local Daemon ↔ Nearest Server (NATS Leaf Node) ✅ Done

**Related docs**: [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (see 2026-07-23 Amendment), [local-durability.feature](docs/bdd/features/local-durability.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Design doc §8](docs/00-design-document.md) (Open Question 2 — resolved; Open Question 1 resolved)

**Entry criteria:** Phase 1 complete. ✅

- [x] Local nats-server instance configured as a leaf node — real leaf-node config (`hub.conf`/`leaf.conf`), validated manually and via `SyncMesh.Sync.Tests`. Connections are currently plaintext/unauthenticated — **explicitly deferred, not a Phase 2 gap**: TLS + registered service credentials is ADR-0002's documented security *baseline* decision, but per the ops/pre-release convention (`ARCHITECTURE.md` → Operational vs. development ownership), wiring it up is out of scope for POC and gates Phase 6, same as the tunnel security review and retention sign-off.
- [x] Local JetStream stream, WorkQueue retention: default ceiling unbounded except by local disk (`Discard: New` on exhaustion) — `SyncMesh.Daemon.Nats.DaemonJetStreamSetup`, configurable via `DaemonNatsOptions` (`IOptions<T>`)
- [x] Publish-on-write from daemon's event writer to local JetStream stream — one-way (daemon → server); `LocalEventWriter` publishes after the local SQLite save succeeds, never mirrors server data back down
- [x] Minimal server-side subscriber: `SyncMesh.ServerHost.Nats.ApplyResponder` — core-NATS request/reply (not JetStream stream mirroring — see ADR-0002 Amendment for why), ack + write to server-tier `EventStoreDbContext`
- [x] Idempotent apply (dedupe by `GlobalEventId`) on the server side
- [x] NATS added to `SyncMesh.AppHost` topology (two container resources, `nats-hub` + `nats-leaf`, real leafnode config files under `src/SyncMesh.AppHost/nats-config/`) — **live-verified end-to-end** (2026-07-23): `dotnet run --project src/SyncMesh.AppHost` brings up Postgres + nats-hub + nats-leaf containers and the `ServerHost`/`Daemon` project processes cleanly; a smoke-test client appended one event through the daemon's IPC pipe and it was confirmed, moments later, as a row in the server-tier Postgres `Events` table — the full Local App → Daemon → SQLite → local leaf → hub → `ApplyResponder` → Postgres path, live, not simulated. (The DCP/container-start limitation seen in Phase 0 did not recur in this session.)
- [x] **Fixed along the way**: `ServerHost`/`Daemon` `Program.cs` never called `Database.MigrateAsync()` — only the BDD test harness applied migrations manually, so a fresh server-tier database had no schema at all outside of tests. Both hosts now migrate their `EventStoreDbContext` on startup before `host.Run()`.

**Exit criteria:**
- [x] `local-durability.feature`: all 9 non-Phase-3/5 scenarios pass (Background + retained-until-ack, removed-after-ack, disk-bound-default, buffered-read, explicit-smaller-cap-override, recording-session-ends-no-residual, no-nearest-server-configured).
- [x] `nearest-neighbor-sync.feature`: the 4 Phase 2 scenarios (on-prem connect, cloud connect with no on-prem tier, config-only switch between them, firewall/NAT outbound-only survival) are step-defined and pass, via `SyncMesh.Bdd.Tests/StepDefinitions/NearestServerContext.cs` + `NearestServerSteps.cs`. The file's remaining 4 scenarios (standalone server, server-mesh reconciliation, intra-site mesh, full-mesh-to-cloud) are correctly still pending — Phase 3 scope.
- [x] Explicit extended-disconnect/reconnect test **exists and passes** — `SyncMesh.Sync.Tests.DaemonToServerSyncTests.ExtendedDisconnectThenReconnect_AllBufferedEventsEventuallyReachTheServer_NoLossNoDuplication`: hub container actually stopped (not a network partition), events written during the outage, hub restarted, all events confirmed applied exactly once with zero loss/duplication. See ADR-0002's 2026-07-23 Amendment for what this proved and why the design sidesteps the specific mirror-sync risk that was originally flagged.
- [x] Live end-to-end verification of the Aspire AppHost NATS topology, outside the BDD/Testcontainers harnesses — see above.

Final full-solution `dotnet build` + `dotnet test` pass — 0 build errors, 0
test failures (2 EventStore.Tests.Sqlite, 2 EventStore.Tests.Postgres, 2
EventStore.Tests.SqlServer, 10 Daemon.Tests, 2 Sync.Tests, 26 Bdd.Tests [12
passed + 14 correctly skipped/pending Phase 3+ scenarios]).

**Deferred to Phase 6 (pre-release), not Phase 2 follow-ups:**
- TLS + registered service credentials for the leaf/gateway connections (ADR-0002/ADR-0004 security baseline) — confirmed out of scope for POC.

## Phase 3 — Server Mesh Reconciliation (Gateways/Supercluster) ✅ Done

**Related docs**: [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (see 2026-07-23 Phase 3 Amendment), [ADR-0003](docs/adr/0003-hybrid-logical-clock-ordering.md), [Data model §3](docs/06-data-model.md) (`HlcGenerator.Merge`), [event-ordering-and-idempotency.feature](docs/bdd/features/event-ordering-and-idempotency.feature), [nearest-neighbor-sync.feature](docs/bdd/features/nearest-neighbor-sync.feature), [Server Mesh Reconciliation diagram](docs/sequence-diagrams.md), [Deployment models](docs/08-deployment-models.md)

**Entry criteria:** Phase 2 complete, at least two server-tier instances available for testing. ✅

Note: a standalone (zero-peer) server is a fully valid, permanent deployment on its own — this phase is about *multi-site* deployments specifically, not something every deployment must eventually adopt.

- [x] Server-mesh replication mechanism — **not** literal NATS `gateway { }` clustering or JetStream cross-cluster mirroring; a point-to-point, per-configured-peer generalization of Phase 2's forwarder/responder pattern instead. See ADR-0002's 2026-07-23 (Phase 3) Amendment for the full rationale and why native gateway/JetStream-mirroring was deliberately not used. Implementation: `SyncMesh.ServerHost.Nats.{ServerMeshOptions,ServerMeshSetup,MeshForwarder}`, plus relay-on-new-insert logic added to `ApplyResponder`.
- [x] Intra-site vs. cross-site topology (full mesh within a site, single/limited designated gateway across sites) — both are the *same* mechanism (`ServerMeshOptions.Peers`), just with more or fewer configured entries; no code branch distinguishes them. Proven directly: a 3-node A–B–C topology where A and C only peer with B (the designated gateway) converges transitively — B relays not just its own locally-originated events but anything it merely *received* from a peer, which is what makes hub-and-spoke shapes converge without full mesh.
- [x] Server-side apply logic — two-way sync: every server both applies incoming peer events and (on a genuinely new insert, regardless of origin) relays onto its own outbound stream for its own peers. Full eventual replication, not consensus — no write blocks on a peer's acknowledgment. Idempotent dedupe by `GlobalEventId` is what stops gossip amplification (an event bounces back to its origin at most once, then the origin's own no-op path stops it going further).
- [x] Replay/read-model query orders by `(HlcPhysicalTicks, HlcLogicalCounter)`, not insertion/arrival order — proven with deliberately out-of-order-arrival HLC values in both the xUnit and BDD suites (see exit criteria below).
- [~] Offline/batch reconciliation mechanism for a standalone site — still undesigned, as flagged in Open Question 4; not attempted this phase, consistent with that note.

**Exit criteria:**
- [x] `event-ordering-and-idempotency.feature` fully passes: duplicate-delivery no-op, out-of-order-arrival replay ordering (Phase 1), reconnection-after-extended-partition with HLC-consistent replay order, and the leaf-node reconnect-sync-gap scenario (full daemon+hub+server harness, not just referenced from `SyncMesh.Sync.Tests`).
- [x] `nearest-neighbor-sync.feature` server-mesh scenarios pass across two+ nodes: standalone (zero peers), 2-node reconciliation, 3-node transitive relay through a designated gateway, and full-mesh-everywhere (3 nodes, every node peering with every other).
- [x] Real integration tests, not mocks: `SyncMesh.Sync.Tests.ServerMeshReconciliationTests` (2-node convergence, 3-node transitive relay, extended peer-outage recovery — real `nats-server` containers throughout, per this project's established "prove it against real infrastructure" convention).
- [x] Final full-solution `dotnet build` + `dotnet test` pass — 0 build errors, 0 test failures (2 EventStore.Tests.Sqlite, 2 Postgres, 2 SqlServer, 10 Daemon.Tests, 5 Sync.Tests, 26 Bdd.Tests [20 passed + 6 correctly skipped/pending Phase 4/5 tunnel-and-monitoring scenarios]).

**Bugs found and fixed along the way** (see `ARCHITECTURE.md` for details):
1. `ApplyResponder`'s `DbUpdateException` catch treated *any* unique-constraint violation as a safe duplicate no-op — only a `GlobalEventId` collision actually is; a `(StreamId, StreamVersion)` collision from a *different* event is a real data-integrity problem and is now rethrown instead of silently swallowed.
2. JetStream's default 30s `AckWait` made a first-attempt mesh-forward race (peer's `ApplyResponder` subscription not yet live) look like a hang in tests; `ServerMeshOptions.AckWait` now defaults to 5s.
3. A step-definition text mismatch from the Phase 2 session ("...leaf node connection **directly to** the cloud cluster" vs. the Switching scenario's "...connection **to** the cloud cluster", no "directly") had silently left that scenario skipped since Task 33 — fixed by adding the missing exact-text overload.
4. Cucumber Expressions treat `/` as alternative-text syntax (`gateway/supercluster` parses as "gateway" OR "supercluster", not the literal string) — had to escape it (`gateway\/supercluster`) in the step attribute to match the feature file's literal text.

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
- [ ] Wire up TLS + registered service credentials for NATS leaf/gateway connections and the tunnel path (ADR-0002/ADR-0004 security baseline) — Phase 2/5 shipped plaintext/unauthenticated by design; this is where that gets closed
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
- **2. Leaf node reconnect-sync reliability.**
  - [x] Phase 2 exit criteria — an explicit extended-disconnect/reconnect
        test exists and passes (`SyncMesh.Sync.Tests`, see above).
  - [ ] Phase 6 requires load/chaos testing under realistic outage
        durations/volumes, beyond this single-scenario proof. Reconnect/
        backoff settings will also be `IOptions<T>`-bound with a smart
        default.
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
