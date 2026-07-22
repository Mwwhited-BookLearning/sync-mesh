# Architecture Notes

Living record of engineering patterns, practices, and conventions adopted
during implementation — the "how we build it" companion to `WORKPLAN.md`
("where we are"). `docs/00-design-document.md` and `docs/adr/` remain the
source of truth for architecture *decisions*; this file is for the
narrower, more code-level conventions that come up while implementing them
(framework choices, project-layout patterns, things that didn't match the
original docs) so they stay consistent across phases instead of being
re-decided ad hoc each time. Update it as new patterns get established —
don't let it go stale.

## Configuration

Every tunable (buffer caps, timeouts, retention, reconnect/backoff, subject
prefixes, etc.) is bound via `Microsoft.Extensions.Options` and consumed as
`IOptions<T>` / `IOptionsMonitor<T>` (the latter where a value may need to
change without a restart) — never read inline via `IConfiguration["..."]`
scattered through application code. Every options class has smart defaults
set on its properties so the app runs sensibly with zero configuration.
Register with `services.AddOptions<T>().Bind(...).ValidateDataAnnotations()`
(or a custom `IValidateOptions<T>`) so invalid configuration fails fast at
startup.

This decides *how* still-open sizing questions (see `WORKPLAN.md` → Open
Questions) get exposed — a bindable, defaulted, validated options class —
without resolving the *values* themselves, which remain product decisions.
Connection strings are the one exception: those stay on the conventional
`IConfiguration.GetConnectionString(...)` / Aspire service-discovery path,
per ASP.NET Core convention, rather than being wrapped in `IOptions<T>`.

**Smart defaults for compliance-adjacent values** (retention periods, audit
windows, and similar): default to commonly recognized industry/regulatory
practice for the relevant domain, not an arbitrary round number — e.g. the
server-tier retention default follows common U.S. healthcare-record
retention practice (see `docs/07-operations-guide.md` → "Retention
default") rather than picking something like "90 days" out of the air. Two
things every such default needs, without exception:
1. **Cite what it's based on**, in the same place the default is set (a
   doc comment above the `IOptions<T>` property, plus the operations
   guide) — a bare number with no rationale is indistinguishable from a
   guess six months later.
2. **State plainly that it's a starting point, not a compliance sign-off.**
   A smart default lets the system ship and run sensibly; it does not
   substitute for a named compliance/legal owner confirming the figure
   against the actual jurisdiction/accreditation this deployment operates
   under. Don't let "we have a default" quietly become "this was decided."

## Sync model & security baseline

- **Buffer cap = floor + configurable ceiling, not one guessed number.**
  Floor: never discard a locally-buffered event before the nearest server
  acks it (WorkQueue retention already gives this). Ceiling: defaults to
  unbounded except by available local disk — store everything until disk
  actually runs out, rather than pre-guessing an outage duration —
  configurable down to an explicit `MaxBytes`/`MaxAge`/`MaxMsgs` via
  `IOptions<T>`. On real disk exhaustion, reject new local writes
  (`Discard: New`); never evict unacknowledged data, since that would
  violate the floor. See `docs/adr/0002-nats-leaf-nodes-for-transport.md`
  Amendment and design doc §4.2.
- **Client↔service hops are one-way in each direction; server↔server is
  two-way.** Local App ↔ Daemon and Daemon ↔ Server are both client↔service
  relationships where client → service (write) and service → client
  (buffered-read response) are each single-directional — never a
  continuous two-way mirror. The daemon never receives a replica of
  server-side data; anything the local app reads back comes from the
  daemon's own local store. Server ↔ server sync (the mesh, when peers are
  configured) is genuinely **two-way**: every connected server both
  publishes its own events and applies incoming events from peers,
  converging to the same fully-replicated history — **full eventual
  replication, not a consensus/quorum-voting mechanism** (no write blocks
  on peer acknowledgment). A standalone server (no peers configured) has
  nothing to sync with, trivially — that's not a one-way restriction, it's
  the degenerate case of "two-way sync among zero peers."
- **Standalone (zero peers) is a first-class, permanent topology, not a
  bootstrapping step.** No deployment is required to eventually join a
  mesh. A standalone site's later reconciliation with others, if it ever
  needs one, may be an offline/batch mechanism instead of a live gateway
  connection — that mechanism is undesigned and is tracked as a separate
  future decision, not assumed to be "just NATS gateways, later." This
  works without redesigning reconciliation because idempotent apply and
  HLC ordering don't depend on *how* an event arrives.
- **No architectural minimum or maximum on server/site/gateway count, and
  no on-prem tier is required at all.** Supported shapes include (not
  exhaustively): a daemon with no nearest server at all ("client
  isolated," permanently — same floor/ceiling buffer behavior as an
  extended outage, just indefinite); a daemon connecting directly to a
  cloud server with zero on-prem servers; a standalone single server;
  multiple servers fully meshed at one site; multiple sites fully meshed
  with each other directly, including cloud; and multiple sites connected
  through a limited designated gateway server per site. None of these are
  mutually exclusive or privileged over another — topology is a
  deployment/config decision, and reconciliation logic must not assume any
  particular shape. See `docs/08-deployment-models.md` for diagrams.
- **Every mesh/tunnel connection is TLS-secured and authenticates with a
  registered service credential scoped to the daemon/server instance —
  never end-user identity/permissions.** This applies uniformly to leaf
  connections, gateway connections, and the Tier X tunnel/relay. A remote
  user's own authorization for what they're allowed to view/control is a
  separate layer on top, not a substitute for this transport-level
  baseline. See `docs/adr/0002-nats-leaf-nodes-for-transport.md` and
  `docs/adr/0004-separate-tunnel-from-event-mesh.md` Amendments.

## Operational vs. development ownership

Where an open question is really an *operations* concern (backup schedules,
retention windows, infrastructure sizing), default to standard, external,
transparent tooling and document the suggested pattern in
`docs/07-operations-guide.md` — don't build it into the application. Only
pull something into development/design scope when it genuinely can't be
externally isolated (the app's own correctness guarantees depend on it —
e.g. purge/retention interacting with idempotent-apply dedupe). See that
doc's worked example for server-tier retention/backup (design doc Open
Question 3).

**Ops/legal/compliance sign-off is a pre-release (Phase 6) concern, out of
scope for POC.** The tunnel security review, retention compliance sign-off,
and real-scale topology decisions all gate production readiness — they
don't gate a POC or earlier implementation phases. A POC ships against the
smart defaults and security baseline already decided in this document, not
a completed sign-off. When adding a new open question of this shape,
default it the same way: decide the mechanism/baseline now, defer the
sign-off to Phase 6, and say so explicitly rather than leaving it
ambiguous which phase actually gates it.

## Testing

- **BDD test framework**: Reqnroll + **MSTest** (not xUnit), specifically
  for `SyncMesh.Bdd.Tests`. xUnit has no pending/inconclusive concept, so a
  scenario with no matching step definition reports as **Failed** — which
  would make `dotnet test` red through every phase until every BDD scenario
  is fully step-defined. MSTest reports undefined steps as **Skipped**,
  keeping the suite green while scenarios are implemented incrementally,
  phase by phase, per `CLAUDE.md`'s "implement against the feature files"
  rule. The `EventStore.Tests.*` provider projects remain on xUnit — plain
  unit tests, no pending-step concern there.
- **Feature files stay in `docs/bdd/features/`** as the single source of
  truth (per `CLAUDE.md`) — linked into `SyncMesh.Bdd.Tests` via an explicit
  `<ReqnrollFeatureFile Include="..." Link="..." />` item rather than
  duplicated into the test project. Because the source lives outside the
  project's directory tree, `ReqnrollUseIntermediateOutputPathForCodeBehind`
  must be `true`, or Reqnroll writes generated `.feature.cs` code-behind
  next to the linked source — i.e. into `docs/bdd/features/` — polluting a
  documentation directory with generated code.
- **Multi-provider EF Core migrations**: EF Core does not support multiple
  providers' migrations living in one assembly — at runtime it applies
  every `Migration` subclass found in the configured migrations assembly,
  regardless of which provider is active, so mixing SQLite/PostgreSQL/SQL
  Server migrations in one project would try to run SQLite-flavored SQL
  against Postgres (or vice versa). Solved by giving each provider its own
  migrations project/assembly
  (`SyncMesh.EventStore.Migrations.{Sqlite,Postgres,SqlServer}`), each with
  its own `IDesignTimeDbContextFactory`, and each provider registration
  extension (`AddSqliteEventStore` etc.) pointing at its own
  `MigrationsAssembly`. Verified per-provider via isolated test projects
  (SQLite in-process, Postgres/SQL Server via Testcontainers, not DCP).

## Local dev environment

- **Orchestration**: Microsoft Aspire (`SyncMesh.AppHost` +
  `SyncMesh.ServiceDefaults`), per explicit request, for a multi-project
  solution. Only Postgres is orchestrated as a container (ServerHost's
  default provider) — SQL Server is supported by ServerHost via config but
  not stood up in the AppHost topology, to keep local dev to one instance.
  NATS joins the topology in Phase 2 when the leaf node is first wired up.
- **Known environment limitation** (observed in this sandboxed dev
  environment, not necessarily elsewhere): Aspire's DCP orchestrator
  successfully starts project resources (Daemon and ServerHost both ran
  cleanly under DCP) but got stuck leaving the Postgres **container**
  resource in `created` state without ever issuing `docker start` — confirmed
  by manually running `docker start` on the exact same container, which
  worked instantly and Postgres booted normally. Testcontainers (used by the
  provider migration tests) is unaffected since it talks to Docker directly,
  not through DCP. This looks like a DCP/Docker interaction quirk specific
  to this sandbox — worth re-verifying `dotnet run --project
  src/SyncMesh.AppHost` in a normal terminal, VS Code, or Visual Studio
  before assuming it's a real bug.
- **`dotnet-ef` tool**: installed as a local tool via
  `.config/dotnet-tools.json` (repo-scoped), not global — run `dotnet tool
  restore` after cloning rather than installing it globally.
- **Target framework**: `net10.0` (current LTS, matches installed SDK
  10.0.301). Solution file is `.slnx` — `dotnet new sln` produces that
  format by default under the .NET 10 SDK; used as-is rather than forcing
  the legacy `.sln` format.

## Dependency hygiene

- **Vulnerability pin**: `SQLitePCLRaw.bundle_e_sqlite3` pinned to `2.1.12`
  in every project that pulls in the SQLite EF Core provider (directly or
  transitively via a project reference), overriding a transitively-resolved
  `2.1.11` with a known high-severity advisory (GHSA-2m69-gcr7-jv3q).

## Documentation

- **Diagrams**: PlantUML embedded as fenced ` ```plantuml ` code blocks in
  Markdown (`docs/c4-diagrams.md`, `docs/sequence-diagrams.md`), not
  standalone `.puml` files — per explicit request. The old
  `docs/c4-diagrams/` and `docs/sequence-diagrams/` folders were removed.
- **Doc set**: `docs/00-design-document.md` (architecture/goals/open
  questions) → `docs/05-implementation-guide.md` (static phased plan) →
  `docs/06-data-model.md` (envelope/entity/HLC shapes) →
  `docs/07-operations-guide.md` (ops-owned vs. dev-owned operational
  concerns) → `docs/adr/` (individual decisions) → `docs/bdd/features/`
  (executable acceptance criteria). `WORKPLAN.md` tracks phase status
  against the implementation guide; this file tracks the conventions above.
