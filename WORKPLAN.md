# Work Plan

Living tracker for implementation progress against `docs/05-implementation-guide.md`.
That document is the authoritative phase definition (entry/exit criteria, BDD
features per phase) — this file tracks *status* and *decisions made along the
way*. Update it as work progresses; do not let it drift from reality.

## Status at a glance

| Phase | Status | Notes |
|---|---|---|
| 0 — Project Setup | ✅ Done | See checklist below |
| 1 — Local Event Store (Daemon Side) | ⬜ Not started | |
| 2 — Local Daemon ↔ Nearest Server (NATS Leaf Node) | ⬜ Not started | |
| 3 — Server Mesh Reconciliation (Gateways/Supercluster) | ⬜ Not started | |
| 4 — Passive Monitoring | ⬜ Not started | |
| 5 — Interactive Tunnel + Relay Fallback | ⬜ Not started | Needs security review scheduled (Open Question 5) |
| 6 — Hardening & Operational Readiness | ⬜ Not started | |

---

## Phase 0 — Project Setup

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

## Decisions & deviations (Phase 0)

- **Target framework**: `net10.0` (current LTS, matches installed SDK 10.0.301).
- **Solution format**: `.sln` requests now produce `.slnx` by default under the
  .NET 10 SDK — used as-is rather than forcing the legacy format.
- **NATS client**: not yet added — deferred to Phase 2, where it's first used.
- **BDD test framework**: Reqnroll + **MSTest** (not xUnit). xUnit has no
  pending/inconclusive concept, so undefined steps report as **Failed**,
  which would make `dotnet test` red through every phase until BDD scenarios
  are fully step-defined. MSTest reports undefined steps as **Skipped**,
  keeping the suite green. The `EventStore.Tests.*` provider projects remain
  on xUnit (plain unit tests, no pending-step concern there).
- **Multi-provider EF Core migrations**: EF Core does not support multiple
  providers' migrations living in one assembly (it applies every `Migration`
  subclass it finds, regardless of active provider). Solved by giving each
  provider its own migrations project/assembly
  (`SyncMesh.EventStore.Migrations.{Sqlite,Postgres,SqlServer}`), each with
  its own `IDesignTimeDbContextFactory`.
- **Local dev orchestration**: Microsoft Aspire (AppHost + ServiceDefaults),
  per explicit request. Only Postgres is orchestrated as a container
  (ServerHost's default provider) — SQL Server is supported by ServerHost via
  config but not stood up in the AppHost topology, to keep local dev to one
  instance. NATS will be added to the AppHost topology when Phase 2 wires up
  the leaf node.
- **Known environment limitation**: in this sandboxed dev environment,
  Aspire's DCP orchestrator successfully starts project resources (Daemon,
  ServerHost ran fine) but got stuck leaving the Postgres **container**
  resource in `created` state without issuing `docker start` — confirmed by
  manually running `docker start` on the same container, which worked
  instantly. Testcontainers (used by the provider migration tests) is
  unaffected since it talks to Docker directly, not through DCP. This looks
  like a DCP/Docker interaction quirk specific to this sandbox — worth
  re-verifying `dotnet run --project src/SyncMesh.AppHost` in a normal
  terminal or Visual Studio.
- **`dotnet-ef` tool**: installed as a local tool via
  `.config/dotnet-tools.json` (repo-scoped), not global — run `dotnet tool
  restore` after cloning.
- **Diagrams**: PlantUML embedded as fenced code blocks in
  `docs/c4-diagrams.md` / `docs/sequence-diagrams.md`, not standalone `.puml`
  files (per explicit request) — the old `docs/c4-diagrams/` and
  `docs/sequence-diagrams/` folders were removed.
- **Vulnerability pin**: `SQLitePCLRaw.bundle_e_sqlite3` pinned to `2.1.12`
  in every project that pulls in the SQLite EF Core provider (directly or
  transitively), overriding a transitively-referenced `2.1.11` with a known
  high-severity advisory (GHSA-2m69-gcr7-jv3q).

## Open questions carried from the design doc (not resolved here)

See `docs/00-design-document.md` §8 for full detail — flagged, not silently
decided, per CLAUDE.md:

1. Buffer cap sizing at the daemon (Phase 6)
2. Leaf node reconnect-sync reliability (Phase 2 exit criteria requires an
   explicit test; Phase 6 requires load/chaos testing)
3. Server-tier retention/backup policy (Phase 6)
4. Full-mesh vs. hub-and-spoke topology at scale (Phase 6)
5. Tunnel relay security model — needs dedicated security review before
   Phase 5 is production-ready
6. WCF/legacy interop boundary scope (Phase 6)
