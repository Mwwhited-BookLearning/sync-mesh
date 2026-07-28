# Work Plan

Living tracker for implementation progress against `docs/05-implementation-guide.md`.
That document is the authoritative phase definition (entry/exit criteria, BDD
features per phase) — this file tracks *status*: what's done, what's
in-flight, what's next. Update it as work progresses; do not let it drift
from reality.

For engineering patterns/practices/conventions adopted along the way (not
phase status), see `ARCHITECTURE.md` instead.

## Status at a glance

**2026-07-27 — Docs restructured**: `sequence-diagrams.md`/`c4-diagrams.md`/
`09-use-cases.md` content is now colocated per-feature in
`docs/bdd/design/*.md` (each feature's design stands on its own — use
case, sequence diagram, relevant C4 excerpt, deployment-model refs, and
the Gherkin source of truth); `docs/bdd/features/*.feature` is now
generated build output (see `ARCHITECTURE.md` → "Feature-doc extraction
tooling"). New feature work should add a companion doc under
`docs/bdd/design/`, not hand-write a `.feature` file.

| Phase | Status | Related docs |
|---|---|---|
| 0 — Project Setup | ✅ Done | [Data model](docs/06-data-model.md), [ADR-0001](docs/adr/0001-event-store-on-ef-core.md) |
| 1 — Local Event Store (Daemon Side) | ✅ Done | [Data model](docs/06-data-model.md), [local-durability.md](docs/bdd/design/local-durability.md) (deferred — see notes; includes Event Recording Flow diagram), [event-ordering-and-idempotency.md](docs/bdd/design/event-ordering-and-idempotency.md) |
| 2 — Local Daemon ↔ Nearest Server (NATS Leaf Node) | ✅ Done | [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (2026-07-23 Amendment), [local-durability.md](docs/bdd/design/local-durability.md), [nearest-neighbor-sync.md](docs/bdd/design/nearest-neighbor-sync.md), [PRODUCTION-HARDENING.md](PRODUCTION-HARDENING.md) (buffer cap sizing — resolved) |
| 3 — Server Mesh Reconciliation (Gateways/Supercluster) | ✅ Done | [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (2026-07-23 Phase 3 Amendment), [ADR-0003](docs/adr/0003-hybrid-logical-clock-ordering.md), [Data model §3](docs/06-data-model.md), [event-ordering-and-idempotency.md](docs/bdd/design/event-ordering-and-idempotency.md), [nearest-neighbor-sync.md](docs/bdd/design/nearest-neighbor-sync.md) (includes Server Mesh Reconciliation diagram) |
| 4 — Passive Monitoring | ✅ Done | [Data model §5](docs/06-data-model.md) (NATS subject naming), [remote-monitoring-tunnel.md](docs/bdd/design/remote-monitoring-tunnel.md) |
| Ancillary — Mesh Monitor Dashboard | ✅ Done (auth deferred to PRODUCTION-HARDENING.md, not phase-numbered) | [ADR-0005](docs/adr/0005-mesh-monitor-dashboard.md), [Design doc §4.6](docs/00-design-document.md), [Data model §6](docs/06-data-model.md), [UI-ARCHITECTURE.md](UI-ARCHITECTURE.md) |
| Ancillary — Event Lineage (Provenance) Schema | ✅ Done | [ADR-0006](docs/adr/0006-event-lineage-descriptive-provenance.md), [Data model §7](docs/06-data-model.md) |
| 5 — Interactive Tunnel + Relay Fallback | ✅ Done | [ADR-0004](docs/adr/0004-separate-tunnel-from-event-mesh.md), [ADR-0007](docs/adr/0007-custom-reverse-tunnel-mechanism.md), [remote-monitoring-tunnel.md](docs/bdd/design/remote-monitoring-tunnel.md) (includes Tunnel Fallback diagram) |
| Ancillary — Order Book Demo (Commands/Queries/CQRS) | ✅ Done | [Data model §8](docs/06-data-model.md), `src/SyncMesh.OrderBook.Api` |
| 6 — Production Hardening | 🚫 Out of scope for this PoC | [PRODUCTION-HARDENING.md](PRODUCTION-HARDENING.md) |

---

## Developer tooling built alongside the phases (not itself a phase)

Not tracked against `docs/05-implementation-guide.md` — pure developer/
operator tooling layered on top of Phase 4's telemetry foundation and the
topology shapes in `docs/08-deployment-models.md`. Full details in
`ARCHITECTURE.md` → "Mesh-wide monitoring dashboard and deployment-model
sandbox" and `UI-ARCHITECTURE.md` (frontend-specific).

- [x] **Mesh monitor web dashboard**: `src/SyncMesh.MeshMonitor.Api`
      (ASP.NET Core + SignalR, subscribes to `monitor.>`, serves a REST
      snapshot + live push) and `web/mesh-monitor` (Vue 3 + Element Plus +
      vis-network topology graph + Pinia-as-ViewModel + `useCommand`).
      `ServerStatus`/`DaemonStatus` self-report each node's own configured
      connections + per-connection event counts, so the dashboard's
      topology is derived from what every node says about itself — no
      separate config to maintain. Backend serves the built frontend from
      its own `wwwroot` (populated automatically on `dotnet build`, not
      just `publish` — see `UI-ARCHITECTURE.md`). Wired into
      `SyncMesh.AppHost` as the `mesh-monitor-api` resource.
      10 Vitest unit tests + 1 Playwright e2e smoke test, all passing,
      plus a backend suite (`tests/SyncMesh.MeshMonitor.Tests`, 9 tests).
      Subscribes to **both** sites' NATS hubs (`MeshMonitorApiOptions
      .NatsUrls`), not just site A's. **Confirmed live** (2026-07-27) in
      the full two-site `SyncMesh.AppHost` topology, once the topology's
      own startup issues were resolved — see `ARCHITECTURE.md` →
      "AppHost dev topology: server tier runs on SQLite" (the earlier
      "DCP hang" diagnosis in this session was wrong; the real cause was
      a Postgres password/data-volume mismatch plus two other Aspire/DCP
      orchestration issues, all fixed).
- [x] **Deployment-model sandbox**: `docker-compose.yml` (repo root, one
      Compose profile per model in `docs/08-deployment-models.md`) +
      `Properties/launchSettings.json` profiles on `SyncMesh.Daemon`/
      `SyncMesh.ServerHost`, one per node-role per model. Mesh-model nodes
      each get their own Postgres database (not shared), so convergence
      is genuinely proven. Smoke-tested `client-onprem` fully live: real
      event, real daemon, real server, real Postgres row. How-to in
      `docs/10-running-deployment-models.md`.

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

**Related docs**: [Data model](docs/06-data-model.md) (`EventEnvelope`, `EventRecord`, `HlcGenerator`, idempotent apply shape), [local-durability.md](docs/bdd/design/local-durability.md) (includes Event Recording Flow diagram), [event-ordering-and-idempotency.md](docs/bdd/design/event-ordering-and-idempotency.md)

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

**Related docs**: [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (see 2026-07-23 Amendment), [local-durability.md](docs/bdd/design/local-durability.md), [nearest-neighbor-sync.md](docs/bdd/design/nearest-neighbor-sync.md), [Design doc §8](docs/00-design-document.md) (Open Question 2 — resolved; Open Question 1 resolved)

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

**Related docs**: [ADR-0002](docs/adr/0002-nats-leaf-nodes-for-transport.md) (see 2026-07-23 Phase 3 Amendment), [ADR-0003](docs/adr/0003-hybrid-logical-clock-ordering.md), [Data model §3](docs/06-data-model.md) (`HlcGenerator.Merge`), [event-ordering-and-idempotency.md](docs/bdd/design/event-ordering-and-idempotency.md), [nearest-neighbor-sync.md](docs/bdd/design/nearest-neighbor-sync.md) (includes Server Mesh Reconciliation diagram), [Deployment models](docs/08-deployment-models.md)

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

## Phase 4 — Passive Monitoring ✅ Done

**Related docs**: [Data model §5](docs/06-data-model.md) (NATS subject naming), [remote-monitoring-tunnel.md](docs/bdd/design/remote-monitoring-tunnel.md)

**Entry criteria:** Phase 2 complete (does not require Phase 3). ✅

- [x] Daemon telemetry/status published to `monitor.<siteId>.<instanceId>.status` — `SyncMesh.Daemon.Nats.MonitorPublisher`, a `BackgroundService` publishing a `SyncMesh.Contracts.DaemonStatus` snapshot (buffered-event count read from the local JetStream stream state, leaf connection state) on a plain core-NATS subject every `DaemonMonitorOptions.PublishInterval` (default 5s). Deliberately **not** JetStream — telemetry is current-state, not a replayable event, so it shares only the daemon's NATS connection with the event-sync path, never its streams or failure semantics (CLAUDE.md working agreement #6).
- [x] `DaemonOptions.InstanceId` added (smart default: machine name) — distinguishes multiple daemon instances that might share one `SiteId`.
- [x] Minimal remote client/CLI: `src/SyncMesh.MonitorClient` — a small console app taking `<nats-url> <siteId> [instanceId]`, subscribing to `monitor.<siteId>.<instanceId>.*` and printing each `DaemonStatus` as it arrives. Manually smoke-tested against a live `nats-server` container end to end (real publish → real subscribe → printed output), not just exercised via the BDD suite's inline replica of the same logic.

**Exit criteria:**
- [x] `remote-monitoring-tunnel.feature` passive-monitoring scenario passes — `SyncMesh.Bdd.Tests.StepDefinitions.{MonitorContext,MonitorSteps}`, a real daemon stack (JetStream setup + `MonitorPublisher`) against a real hub+leaf NATS pair, with a subscriber connected on the hub side standing in for "the remote user" — exactly where a real monitoring client would connect, proving the telemetry crosses the leaf boundary via ordinary NATS routing with zero separate infrastructure. The other 5 scenarios in this feature file (direct tunnel, relay fallback, TLS/service-credential auth, both cross-failure-isolation scenarios) are Phase 5/6 scope and remain correctly pending.
- [x] Final full-solution `dotnet build` + `dotnet test` pass — 0 build errors, 0 test failures (2 EventStore.Tests.Sqlite, 2 Postgres, 2 SqlServer, 10 Daemon.Tests, 5 Sync.Tests, 26 Bdd.Tests [21 passed + 5 correctly skipped/pending Phase 5 tunnel scenarios]).

## Phase 5 — Interactive Tunnel + Relay Fallback ✅ Done

**Related docs**: [ADR-0004](docs/adr/0004-separate-tunnel-from-event-mesh.md) (see Amendment), [ADR-0007](docs/adr/0007-custom-reverse-tunnel-mechanism.md), [remote-monitoring-tunnel.md](docs/bdd/design/remote-monitoring-tunnel.md) (includes Tunnel Fallback diagram)

**Entry criteria:** Phase 2 complete. ✅ This phase ships the tunnel
*mechanism* only — a custom, plain-TCP, direct-first/relay-fallback
reverse tunnel (ADR-0007). TLS + registered service credentials — the
decided baseline (ADR-0004's Amendment) — are deferred wholesale to
`PRODUCTION-HARDENING.md`, matching exactly how Phase 2/3 shipped the
NATS leaf/gateway connections plaintext/unauthenticated by design.

- [x] Tunnel/relay mechanism integrated separately from the NATS event
      mesh — plain TCP for this phase. `SyncMesh.Daemon.Tunnel.TunnelAgent`
      (direct listener + outbound-only control connection to the relay,
      same "daemon dials out" pattern as the NATS leaf node) and
      `SyncMesh.ServerHost.Tunnel.TunnelRelay` (agent registry + client
      pairing). Wire framing (`SyncMesh.Contracts.Tunnel.TunnelFraming`)
      used only for control-connection signaling — the tunneled byte
      stream itself is always raw and unframed, forwarded to a
      configurable local target endpoint (protocol-agnostic, same approach
      `frp`/`chisel` themselves use). One active session per daemon at a
      time — a deliberate POC simplification (ADR-0007), not a hard
      limit. `TunnelStatus` telemetry rides the already-reserved
      `tunnel.<siteId>.<instanceId>.control` subject, current-state only,
      same convention as `DaemonStatus`/`ServerStatus`. See ADR-0007 for
      the full design and `PRODUCTION-HARDENING.md` for what's explicitly
      deferred (TLS, service-credential auth, the full security review).
- [x] Direct-connection-first, relay-fallback logic on the client side —
      `SyncMesh.TunnelClient.TunnelConnector` (a new console project
      mirroring `SyncMesh.MonitorClient`'s shape), shared (not
      re-implemented) by `SyncMesh.Sync.Tests` and `SyncMesh.Bdd.Tests` via
      a `ProjectReference`, so the fallback behavior under test is the
      literal shipped code.
- [x] Explicit chaos-style tests: kill tunnel path, confirm event-sync
      unaffected, and vice versa — `SyncMesh.Sync.Tests.TunnelFailureIsolationTests`
      (`TunnelKilled_EventSyncUnaffected`, `EventSyncKilled_TunnelUnaffected`),
      real NATS hub+leaf containers alongside the real tunnel mechanism,
      asserting actual byte-for-byte round-trips through a TCP echo
      target, not just "a connection was established." Both directions
      prove independence architecturally, not just by assertion: nothing
      in `Daemon/Tunnel`/`ServerHost/Tunnel` references `NatsConnection`/
      `NatsJSContext` or anything in `Daemon/Nats`/`ServerHost/Nats`, and
      vice versa.

**Exit criteria:**
- [x] `remote-monitoring-tunnel.feature`'s 4 Phase-5-scope scenarios pass
      (direct connection, relay fallback, both cross-failure-isolation
      scenarios) via `SyncMesh.Bdd.Tests.StepDefinitions.{TunnelContext,TunnelSteps}`
      — real `TunnelAgent`/`TunnelRelay`/echo target always, real NATS
      hub+leaf added only for the two cross-failure scenarios. The
      TLS/service-credential scenario remains correctly pending — see
      `PRODUCTION-HARDENING.md`.

Final full-solution `dotnet build` + `dotnet test` pass — 0 build errors,
0 test failures (2 EventStore.Tests.Sqlite, 2 Postgres, 2 SqlServer, 10
Daemon.Tests, 7 Sync.Tests [5 existing + 2 new
`TunnelFailureIsolationTests`], 26 Bdd.Tests [25 passed + 1 correctly
skipped/pending Phase 6 TLS/service-credential scenario]).

**Bugs found and fixed along the way** (see `ARCHITECTURE.md` for
details):
1. `TunnelAgent`'s direct listener and `TunnelRelay`'s both listeners
   originally bound `IPAddress.Any` (IPv4-only). A remote client
   connecting to `"localhost"` can resolve to `::1` first on some
   machines/environments, producing a fast, spurious "direct connection
   failed" that incorrectly triggered relay fallback in the "direct
   connection succeeds" BDD scenario. Fixed by binding all three
   listeners (and the test suites' TCP echo target) dual-stack
   (`TunnelSockets.CreateDualStackListener`), independent of address-
   family resolution order.
2. Three step-definition texts used a literal `/` inside step text
   ("firewall/NAT", "tunnel/relay", "tunnel/monitoring") — the same
   Cucumber Expressions "`/` means alternative text" trap already
   documented from Phase 3 (see `ARCHITECTURE.md`), left three of the four
   new tunnel scenarios silently unbound (reporting as Skipped) until the
   slashes were escaped (`firewall\/NAT`, etc.).

## Phase 6 — Production Hardening (out of scope for this PoC)

This repo is a PoC/teaching example, not a path to production — nothing
in this phase is expected to actually be built here. See
[`PRODUCTION-HARDENING.md`](PRODUCTION-HARDENING.md) for the full,
consolidated list of what a real deployment would still need (TLS +
service-credential wiring, the tunnel security review, retention/
compliance sign-off, realistic-scale chaos/load testing, the real-world
gateway-count decision, and offline/batch reconciliation design).

## Ancillary Work

Work outside the numbered phase plan — additive/layered on top of already-
closed phases, not a reopening of their exit criteria.

### Mesh Monitor Dashboard ✅ Done (auth deferred to PRODUCTION-HARDENING.md)

See [ADR-0005](docs/adr/0005-mesh-monitor-dashboard.md) (Accepted, per its
2026-07-27 Amendments) and "Developer tooling built alongside the phases"
above for the full, current status. As of 2026-07-27: backend, frontend
(Vue 3 + Element Plus + vis-network), frontend test coverage, and a
backend test project (`tests/SyncMesh.MeshMonitor.Tests` — `TopologyStore`
fold logic + `MonitorSubscriber` parsing, 9 tests) are all built; the
dual-hub telemetry gap (only site A's NATS hub was subscribed to) is
fixed (`MeshMonitorApiOptions.NatsUrls`, one subscribe loop per site); and
the live two-site AppHost topology has been confirmed running end to end,
this dashboard included. Authentication on `/api/topology`/the SignalR
hub remains deferred to `PRODUCTION-HARDENING.md`, not tracked as a gap
here.

### Event Lineage (Provenance) Schema ✅ Done

See [ADR-0006](docs/adr/0006-event-lineage-descriptive-provenance.md).
Additive, backward-compatible schema change layered on top of Phase 1's
already-closed exit criteria — an event can descriptively reference the
prior event(s) its data was sourced from (many-to-many), purely for
audit/traceability. Does not affect idempotent apply, HLC ordering, or
replay in any way. `EventLineage` entity + DbContext wiring, migrations
across all three providers (SQLite/Postgres/SQL Server), `EventEnvelope`/
`AppendEventRequest` wire contract additions, `LocalEventWriter`/
`ApplyResponder` write/apply-path threading, and migration tests are all
complete — see `docs/06-data-model.md` §7 for the full shape.

### Order Book Demo (Commands/Queries/CQRS) ✅ Done

An example domain — "stock trading with an order book" — built on the
generic `EventEnvelope`/`EventRecord` machinery specifically to demonstrate
commands → events → a genuine CQRS read model and mesh convergence you
can watch happen, not just prove in tests (see recent audit finding: the
project's own "event sourcing + CQRS" claim didn't hold up until this —
`LocalEventReader` alone just re-queries the write-side table). See
`docs/06-data-model.md` §8 for the full design, especially why
`StreamId = OrderId` (one order = one stream, never shared across
origins) is a load-bearing constraint, not an arbitrary choice.

- **`SyncMesh.Contracts.OrderBook`** — the two example domain events
  (`OrderPlaced`, `OrderCancelled`).
- **`SyncMesh.OrderBook.Api`** (new project) — command endpoints
  (`POST /api/orders`, `POST /api/orders/{id}/cancel`, routing through the
  correct site's daemon via `LocalIpcClient` — reused as-is, it already
  existed as "the reference client until a real local app exists"), query
  endpoints (`GET /api/orderbook(/​{symbol})`, served from
  `IOrderBookStore`), `OrderBookProjector` (the actual read model — polls
  one server's `EventStoreDbContext`, folds `OrderPlaced`/`OrderCancelled`
  into a denormalized in-memory book), and a self-contained
  `wwwroot/index.html` test UI (place/cancel orders, poll the book every
  2s) — deliberately plain HTML/JS, no SPA framework, since this is
  explicitly "a test UI."
- **`SyncMesh.Daemon.Demo.SyntheticOrderGenerator`** — on by default,
  generates/cancels random orders in-process so "leaf nodes generating
  data replicated across the mesh" is something you can watch happen as
  soon as the topology runs, not something you have to trigger manually.
- **`SyncMesh.Daemon.Demo.MarketDataOrderGenerator`** — an independent,
  config-selectable alternative order source using real, live-fetched
  stock prices (Twelve Data's free `/price` endpoint) instead of random
  noise. See [ADR-0008](docs/adr/0008-live-market-data-generator.md) —
  this project's first dependency on a live external network service,
  flagged explicitly rather than folded in as "just another generator."
  Zero-setup default (`ApiKey="demo"`, `Symbols=["AAPL"]`) verified
  directly against the live API before shipping; degrades to "skip this
  tick, log a warning" on any network/API failure, same as every other
  background publisher in this codebase.
- **`SyncMesh.AppHost` expanded to a full two-site multi-server mesh** —
  previously wired exactly one daemon + one server; now two complete
  sites (`site-a`/`site-b`, each own SQLite event-store file — see
  [ADR-0001](docs/adr/0001-event-store-on-ef-core.md)'s Amendment for why
  this dev topology's server tier runs on SQLite rather than Postgres —
  NATS hub+leaf pair, `ServerHost`, `Daemon`), with the two `ServerHost`s
  peered directly (`ServerMeshOptions.Peers`, the same point-to-point
  mechanism proved in Phase 3's tests, now live in the dev topology for
  the first time — and confirmed live, 2026-07-27: an order placed
  through site-b's daemon appeared in `orderbook-api`'s read model, built
  from *site A's* database only, within a few seconds; cancel routed back
  through site B correctly too). `orderbook-api`'s read model polling
  only site A's database — with site B's orders converging into it — is
  the concrete proof the mesh's convergence promise holds, not left as
  "should work in theory."
- **Deliberately no trade matching** — a real distributed matching engine
  needs strong consistency this mesh's design explicitly doesn't provide
  (see `ARCHITECTURE.md`'s "full eventual replication, not consensus"
  principle); building it would contradict the architecture, not
  demonstrate it. Confirmed with the user before implementation.
- **Test coverage**: `tests/SyncMesh.OrderBook.Tests` — unit tests of
  `OrderBookStore`'s fold logic only (place/cancel/sort/convergence), no
  BDD/Testcontainers suite — a deliberate scope choice, matching
  `SyncMesh.MeshMonitor.Api`'s own unit-tests-only coverage
  (`tests/SyncMesh.MeshMonitor.Tests`); this is a worked example, not a
  phase deliverable with entry/exit criteria.

---

## Open questions carried from the design doc

Fully consolidated into [`PRODUCTION-HARDENING.md`](PRODUCTION-HARDENING.md)
— buffer cap sizing (resolved), leaf-node reconnect-sync reliability,
server-tier retention/backup policy, full-mesh vs. hub-and-spoke at
scale, tunnel relay security model, and WCF/legacy interop (resolved) all
live there now instead of being duplicated in both `docs/00-design-document.md`
§8 and here.
