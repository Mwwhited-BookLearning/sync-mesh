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

## Tier 0 IPC (Local App ↔ Local Daemon)

- **Transport**: plain named pipes via `System.IO.Pipes`
  (`NamedPipeServerStream`/`NamedPipeClientStream`), not gRPC. Both are
  explicitly allowed per `docs/00-design-document.md` §4.1, and named pipes
  are simpler here: no Kestrel/ASP.NET Core hosting, no protobuf toolchain,
  and `System.IO.Pipes` already works cross-platform (Unix-domain-socket-
  backed on Linux/macOS) without OS-conditional code. Revisit if Phase 1's
  needs outgrow a simple request/response protocol (e.g. server-push
  streaming to the local app) — gRPC over a named pipe/UDS Kestrel
  transport remains the natural upgrade path.
- **Wire protocol**: a 4-byte length prefix + UTF-8 JSON, one request/
  response per connection (`SyncMesh.Daemon.Ipc.IpcFraming`). No
  request multiplexing over a single connection — simplicity over
  throughput, appropriate for a local, single-digit-events-per-second IPC
  hop.
- **Request handling**: each connection gets its own DI scope
  (`IServiceScopeFactory.CreateScope()`), so `EventStoreDbContext` (scoped)
  is never shared across concurrent requests, while `HlcGenerator` stays a
  singleton (its counter must be monotonic across everything this daemon
  process produces).

## Daemon → server forwarding (NATS leaf node)

- **Pull-consume + core-NATS request/reply, not JetStream stream
  mirroring.** `SyncMesh.Daemon.Nats.EventForwarder` pull-consumes the
  local WorkQueue stream and sends each event as a plain core-NATS request
  to the hub; `SyncMesh.ServerHost.Nats.ApplyResponder` replies once it's
  idempotently applied. The JetStream message is acked only on a
  successful reply — never on send. This deliberately avoids JetStream's
  built-in cross-leaf stream mirroring/sourcing feature, which is the
  specific mechanism ADR-0002's original risk note and design doc Open
  Question 2 were worried about ("known reports of gaps in leaf-node
  mirror sync"). Plain core-NATS pub/sub and request/reply already cross
  the leaf-node boundary transparently with no special config — see
  ADR-0002's 2026-07-23 Amendment for how this was validated (manual
  two-container smoke test, then an automated stop/restart-the-hub test in
  `SyncMesh.Sync.Tests`).
- **A background consume loop must not die silently on a fault.**
  `EventForwarder` wraps its `await foreach` in an outer retry loop —
  early versions let a single faulted pull-request exit `ExecuteAsync` for
  good, silently stranding every buffered event un-acked even after the
  hub recovered. A `BackgroundService` that can exit early from an
  unhandled exception is a bug in itself, independent of what caused the
  original fault.
- **Testing container restarts needs a fixed host port, not a random
  one.** Testcontainers' dynamic port allocation does not reliably survive
  a `StopAsync`/`StartAsync` cycle on the same container — the previously
  mapped host port can become unreachable after restart. This is a test-
  harness artifact, not a real NATS/production concern (a real hub's
  address doesn't change under it), but it will manifest as exactly the
  kind of "never recovers after reconnect" failure you'd wrongly attribute
  to the leaf-node/forwarder design if you don't know to check for it
  first. Any test that stops/starts a container it will reconnect to later
  needs an explicit fixed `WithPortBinding(hostPort, containerPort)` for
  that container specifically.
- **`NatsJSContext.PublishAsync` does not throw on a server-side
  rejection by itself** (e.g. the stream's configured `MaxMsgs`/`MaxBytes`
  cap is full, with `Discard: New`) — it returns a `PubAckResponse` that
  must be checked; only calling `.EnsureSuccess()` on that response throws
  (`NatsJSApiException`). Discovered because a capacity-cap test's
  "expect rejection" assertion silently found nothing to catch until
  `EnsureSuccess()` was added to `LocalEventWriter`. Any code path that
  calls `PublishAsync` and cares whether the publish actually succeeded
  must call `EnsureSuccess()` (or otherwise inspect the ack) — awaiting
  the call alone proves nothing.
- **Worker hosts must migrate their own `EventStoreDbContext` on
  startup.** `SyncMesh.ServerHost` and `SyncMesh.Daemon` originally relied
  entirely on the BDD/integration test harnesses calling
  `Database.MigrateAsync()` manually — nothing in either host's own
  `Program.cs` did. Invisible in every automated test (the harness always
  migrates), but a live Aspire AppHost run against a fresh Postgres
  container had zero tables and the server-tier writes had nowhere to
  land. Fixed by adding an explicit `await
  scope.ServiceProvider.GetRequiredService<EventStoreDbContext>()
  .Database.MigrateAsync()` in both `Program.cs` files, before
  `host.Run()`. Any new host that owns an `EventStoreDbContext` needs the
  same startup step — don't assume it's someone else's job.

## Server-mesh replication (NATS gateway hop, Phase 3)

- **Point-to-point per-peer forwarding, not native NATS `gateway { }`
  clustering or JetStream cross-cluster mirroring.** Same category of
  decision as the leaf-node hop above, extended to server↔server: each
  `ServerHost` owns a **local-only** JetStream stream (`MESH_OUTBOUND`,
  see `SyncMesh.ServerHost.Nats.ServerMeshSetup`) and runs one
  `MeshForwarder` loop per configured peer (`ServerMeshOptions.Peers`),
  dialing that peer's URL directly and forwarding via plain core-NATS
  request/reply to its `ApplyRequestSubject` — the exact same endpoint a
  daemon uses. No code distinguishes "a request from a daemon" from "a
  request from a peer server." See `docs/adr/0002-nats-leaf-nodes-for-
  transport.md`'s 2026-07-23 (Phase 3) Amendment for the full rationale.
- **Interest retention, not WorkQueue, for `MESH_OUTBOUND`.** Multiple
  independent peers each need their own copy of every event; WorkQueue
  (ack-by-any-consumer removes it for everyone) is wrong here. A durable
  consumer per peer (`TO_<peerSiteId>`) must be provisioned *before* any
  message is published, or Interest retention has no registered interest
  to hold it for.
- **Gossip + idempotent dedup is what makes hub-and-spoke topologies
  converge, not full mesh.** `ApplyResponder` relays to `MESH_OUTBOUND`
  on *any* genuinely-new insert — regardless of whether the event
  originated from this server's own daemons or arrived from a peer. This
  is what lets a designated "gateway" server relay events it merely
  *received* onward to its own other peers (proven with a 3-node A–B–C
  test where A and C only peer with B). The dedupe-by-`GlobalEventId`
  no-op path is what stops this from amplifying forever: an event can
  bounce back to its origin at most once.
- **A `DbUpdateException` on insert is not automatically a safe
  duplicate.** Only a `GlobalEventId` collision (the primary key) is —
  that's a legitimate race between, say, a daemon's direct write and a
  peer's gossiped copy of the same event arriving concurrently. A
  `(StreamId, StreamVersion)` collision from a *different* `GlobalEventId`
  is a real data-integrity problem and must be rethrown, not silently
  swallowed. Found via a test bug (two different simulated events
  sharing a `StreamId` with the same hardcoded `StreamVersion`) that the
  original catch-and-return handling masked completely — `ApplyResponder`
  now re-checks whether *this specific* `GlobalEventId` is what's present
  before treating the exception as a no-op.
- **JetStream's default 30s `AckWait` is too slow for inter-server
  forwarding.** A transient first-attempt race (the peer's `ApplyResponder`
  subscription not yet live when the forwarder's first pull lands) looks
  identical to a real outage until the 30s redelivery fires — `ServerMeshOptions.AckWait`
  defaults to 5s instead, tuned specifically against this race.
- **Cucumber Expressions treat `/` as alternative-text syntax.** A step
  attribute text of `"...gateway/supercluster connection"` parses as
  "gateway" OR "supercluster", not the literal string with a slash in it —
  it silently fails to match a feature file step containing that same
  literal text. Escape it (`gateway\/supercluster`) to match literally.

## Passive monitoring (Phase 4)

- **Telemetry rides plain core-NATS pub/sub, deliberately never
  JetStream.** `SyncMesh.Daemon.Nats.MonitorPublisher` publishes a
  `DaemonStatus` snapshot to `monitor.<siteId>.<instanceId>.status` on the
  daemon's existing leaf connection — no separate stream, no ack, no
  retention policy. Current-state telemetry has nothing to replay; the
  next tick supersedes a missed one, so there's no durability contract to
  uphold here, unlike the event-sync path. This is also what keeps the two
  paths' failure domains genuinely separate (CLAUDE.md working agreement
  #6): a JetStream problem on the event side can't touch monitoring, and
  vice versa, because they don't share a stream.
- **A remote monitoring client connects on the server/hub side, never
  directly to a daemon's leaf.** Same interest-graph routing that already
  carries event-sync traffic across the leaf boundary (validated in Phase
  2/3) carries `monitor.*` subjects too, with zero additional
  configuration — this is the concrete thing "no separate infrastructure
  needed" (design doc §4.5) means in practice.
- **`127.0.0.1` can hang where `localhost` works, for a directly-`docker
  run -p`-published port in this sandbox.** Manually smoke-testing
  `SyncMesh.MonitorClient` against a container started with a bare `docker
  run -p hostPort:4222` (not via Testcontainers) timed out connecting to
  `127.0.0.1:hostPort` — a raw `/dev/tcp` probe to the same address from
  Bash hung for the full 2-minute command timeout, while the identical
  probe against `localhost:hostPort` succeeded immediately. Every
  Testcontainers-based test in this repo is unaffected (`container
  .Hostname` never resolves to a bare `127.0.0.1` literal), so this only
  bites ad hoc manual verification against a directly-run container —
  prefer `localhost` over `127.0.0.1` when doing that in this environment.

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
- **A scenario's `Background` gates every scenario in that feature file.**
  If the `Background` asserts infrastructure that doesn't exist yet in the
  current phase (e.g. `local-durability.feature`'s Background asserts "an
  embedded NATS leaf node" and JetStream WorkQueue retention, both Phase 2),
  don't bind those steps early just to turn the suite green — that means
  asserting something false. Leave the whole file pending until its
  Background is literally true, and prove the underlying property (e.g.
  durable local storage surviving a restart) via an ordinary unit/
  integration test in the meantime. `WORKPLAN.md` notes which feature files
  are deferred this way and why, per phase.
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
