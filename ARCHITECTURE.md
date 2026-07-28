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

## Interactive tunnel + relay (Phase 5)

See `docs/adr/0007-custom-reverse-tunnel-mechanism.md` for the full
design. Conventions specific to `SyncMesh.Daemon.Tunnel`/
`SyncMesh.ServerHost.Tunnel`:

- **Outbound-only control/data connections — no inbound firewall rule
  needed, same as the NATS leaf node (ADR-0002).** `TunnelAgent` always
  dials its relay; the relay never dials a daemon. The direct listener is
  the one exception (a remote client dials in directly when network
  topology allows it), which is exactly the "fast path" the mechanism is
  built to try first.
- **Wire framing is signaling-only; the tunneled byte stream is always
  raw and unframed.** `SyncMesh.Contracts.Tunnel.TunnelFraming`'s 5-byte
  header frames (`Hello`/`Heartbeat`/`OpenDataChannel`/`DataChannelHello`/
  `ClientHello`) exist only on control connections and the handshake byte
  of a new data/client connection — once a session is spliced, both ends
  just `CopyToAsync` raw bytes to `LocalTargetEndpoint`. This is what
  keeps the mechanism protocol-agnostic (works identically for RDP/VNC/
  raw TCP/anything else per design doc §4.5).
- **One active session per daemon, gated on both sides.** A
  `SemaphoreSlim(1,1)` on the agent (covers both the direct listener and
  data-channel requests) and a per-agent semaphore on the relay (checked
  *before* ever asking the agent to open a data channel, so a busy agent
  never even sees a second request) — a deliberate POC simplification
  (ADR-0007), not a hard design limit.
- **Same outer-retry-loop convention as `EventForwarder`/`MeshForwarder`.**
  Both `TunnelAgent`'s two loops (direct listener, control connection) and
  `TunnelRelay`'s two loops (agent listener, client listener) restart on
  fault after a fixed 2s delay rather than letting `ExecuteAsync` exit —
  the same "a background consume loop must not die silently" lesson
  applied to a fourth mechanism now.
- **`TunnelStatus` telemetry rides the already-reserved
  `tunnel.<siteId>.<instanceId>.control` subject as pure current-state
  telemetry**, structurally identical to `DaemonStatus`/`ServerStatus`
  (`TunnelStatusPublisher`, same `PeriodicTimer` + plain core-NATS publish
  pattern as `MonitorPublisher`) — despite the "control" name, no real
  session-establishment signaling ever rides on NATS; that all lives
  inside the plain-TCP mechanism.
- **Zero reference to `NatsConnection`/`NatsJSContext` anywhere in either
  `Tunnel/` folder, and vice versa in `Nats/`.** This is what makes the
  tunnel's failure domain independent of event-sync *architecturally*, not
  just by assertion — proven directly by
  `SyncMesh.Sync.Tests.TunnelFailureIsolationTests`, which kills each
  mechanism in turn and exercises the other against real infrastructure.
- **Dual-stack (IPv4 + IPv6) listeners, not `IPAddress.Any`.** All three
  TCP listeners (`TunnelAgent`'s direct listener, `TunnelRelay`'s agent
  and client listeners) — plus the test suites' TCP echo target — bind via
  `SyncMesh.Contracts.Tunnel.TunnelSockets.CreateDualStackListener`.
  Binding `IPAddress.Any` (IPv4-only) let `"localhost"` resolving to `::1`
  first produce a fast, spurious connection failure on some
  machines/environments — this surfaced as the "direct connection
  succeeds" BDD scenario incorrectly falling back to relay every time,
  not a hang, so it was easy to misattribute to the fallback logic itself
  rather than the listener's address family.
- **TLS + service-credential authentication are explicitly not
  implemented this phase** — see `PRODUCTION-HARDENING.md`. `TunnelAgent`/
  `TunnelRelay` accept any connection that speaks the wire framing
  correctly; nothing about identity or transport encryption is checked.
- **No new shared mechanism project.** Mirrors the `EventForwarder`/
  `ApplyResponder` precedent — mechanism code lives directly in the
  existing `SyncMesh.Daemon`/`SyncMesh.ServerHost` host projects (in a new
  `Tunnel/` sibling to each project's `Nats/` folder); only the wire
  framing and `TunnelStatus` contract are genuinely shared, and those
  already belong in `SyncMesh.Contracts` alongside `EventEnvelope`.
- **`SyncMesh.TunnelClient.TunnelConnector` (the direct-first/relay-
  fallback logic) is referenced, not re-implemented, by the test
  suites.** `SyncMesh.Sync.Tests` and `SyncMesh.Bdd.Tests` both take a
  `ProjectReference` to `SyncMesh.TunnelClient`, so the fallback behavior
  under test is the literal shipped code — a deliberate improvement over
  `MonitorContext`'s precedent of re-implementing `MonitorClient`'s
  subscribe logic inline.

## Mesh-wide monitoring dashboard and deployment-model sandbox (developer tooling)

Two additions that aren't part of the phased implementation guide
(`docs/05-implementation-guide.md`) — pure developer/operator tooling
built on top of the Phase 4 telemetry mechanism and the topology shapes in
`docs/08-deployment-models.md`. See `docs/adr/0005-mesh-monitor-dashboard.md`
and design doc §4.6 for what the dashboard is and why it's separate from
per-instance remote monitoring (§4.5).

- **`src/SyncMesh.MeshMonitor.Api`** (ASP.NET Core + SignalR) subscribes to
  `monitor.>` and serves both a REST snapshot (`GET /api/topology`) and a
  live push (`MeshMonitorHub`) to **`web/mesh-monitor`**, a Vue 3 +
  Element Plus + vis-network dashboard — see `UI-ARCHITECTURE.md` for the
  frontend's own conventions (component file split, MVVM translation,
  testing). `ServerStatus`/`DaemonStatus` (`SyncMesh.Contracts`) self-
  describe each node's own configured connections and per-connection
  event counts, so the whole topology is derived from what every node
  already reports about itself — no separate topology config to maintain.
  Backend-specific conventions:
  - **In-memory topology store, no durability, by design** —
    `ITopologyStore` is a `ConcurrentDictionary` keyed by `siteId:instanceId`;
    a dashboard restart just re-learns the topology from the next round of
    `monitor.*` ticks, the same "nothing to replay" reasoning Phase 4's
    telemetry itself already relies on.
  - **SignalR is push-only.** `MeshMonitorHub` has no client-callable
    methods — the browser only listens for `NodeUpdated`; the REST snapshot
    endpoint (`GET /api/topology`) covers a freshly opened tab's first paint,
    SignalR covers everything after.
  - **Same outer-retry-loop convention as `EventForwarder`/`MeshForwarder`.**
    `MonitorSubscriber`'s NATS subscribe loop must not silently die on a
    fault — same bug class already fixed once in `EventForwarder` (see
    Daemon → server forwarding above); it was applied here from the start
    rather than rediscovered.
  - **Dev-only CORS.** The `DevCors` policy (allowing `localhost:5173`,
    Vite's dev server origin) only applies in `IsDevelopment()` — the
    built/production path serves the SPA same-origin from `wwwroot`
    (populated automatically on `dotnet build`, not just `publish` — see
    `UI-ARCHITECTURE.md`), no CORS needed there.
  - **No authentication yet.** Unlike every other cross-instance
    connection in this project, `/api/topology` and the SignalR hub
    currently have no auth — tracked in `PRODUCTION-HARDENING.md`, not an
    intentional exception to the TLS + service-credential baseline below.
- **`docker-compose.yml` (repo root) + `Properties/launchSettings.json`
  profiles** on `SyncMesh.Daemon`/`SyncMesh.ServerHost` let any of the six
  documented deployment models be stood up by hand for manual
  observation — see `docs/10-running-deployment-models.md`. Mesh-model
  nodes (intra-site-mesh, full-mesh) each get their own Postgres database
  (not a shared one) specifically so convergence is genuinely proven
  across independently-stored history, consistent with `ServerHost`
  remaining Postgres/SqlServer-only at the server tier (no SQLite carve-
  out was added for this sandbox — provisioning one database per node was
  the correct fix, not loosening that convention).

## Event lineage (provenance)

See `docs/adr/0006-event-lineage-descriptive-provenance.md` and design
data model `docs/06-data-model.md` §7.

- **No DB-enforced foreign key, by design.** `EventLineage(ChildEventId,
  ParentEventId)` has a composite PK and one secondary index
  (`ParentEventId`) — nothing else. A hard FK to `EventRecord.GlobalEventId`
  would reject a child event's apply purely because its parent hasn't
  arrived at this node yet via a different gossip path, which is a benign,
  expected race under this project's out-of-order/at-least-once delivery
  model — not a real integrity violation. Referential integrity is
  enforced only at authorship time, in `LocalEventWriter`, against that
  daemon's own local store.
- **`ApplyResponder`'s relay path needed zero changes.** It forwards the
  original serialized `EventEnvelope` bytes verbatim onto `MESH_OUTBOUND` —
  `ParentEventIds` is just another field on that same JSON payload, so it
  crosses the whole mesh automatically.
- **Lineage rows ride the same idempotency gate as `EventRecord`.** Both
  `LocalEventWriter` and `ApplyResponder` only add `EventLineage` rows
  alongside a genuinely-new `EventRecord` insert (never on the
  duplicate/no-op paths), so lineage can never be double-inserted for the
  same event without any dedicated dedupe logic of its own.
- **Retry-loop entities must be detached together, not just the primary
  one.** `LocalEventWriter`'s optimistic-concurrency retry loop builds a
  fresh `EventRecord` (and now, fresh `EventLineage` rows) each attempt;
  on a `DbUpdateException`, every entity added *in that attempt* —
  `record` and its `lineageRows` — must be detached before retrying, or
  the abandoned attempt's tracked entities dangle into the next one. Only
  detaching `record` (as the original pre-lineage code did) would leave
  stale tracked `EventLineage` instances behind.

## Order Book demo (example domain — commands, queries, CQRS)

See `docs/06-data-model.md` §8 for the full design. Conventions specific
to `SyncMesh.OrderBook.Api`/`SyncMesh.Daemon.Demo`:

- **`StreamId = OrderId` — the load-bearing lesson for any future example
  domain built on this event store.** `StreamVersion` is computed from
  each daemon's own *local* table only, with no cross-site coordination —
  two different daemons writing to the *same* `StreamId` would each
  independently claim overlapping version numbers, and `ApplyResponder`
  correctly treats that collision as a genuine data-integrity error, not a
  safe duplicate. One order = one stream, owned by whichever daemon
  places it, is what avoids this entirely. Don't design a future example
  domain around a stream shared across origins; fold many single-origin
  streams into a read model instead, same as here.
- **`OrderBookProjector` is a genuine CQRS read model, not just re-reading
  the write-side table.** Unlike `SyncMesh.Daemon.Ipc.LocalEventReader`
  (which queries the same `EventRecord` table the write path inserts
  into), `OrderBookProjector` polls that table and folds matching events
  into `OrderBookStore` — a separate, denormalized, in-memory structure
  keyed by `OrderId`. This is what actually closes the gap the project's
  "event sourcing + CQRS" framing had been claiming without evidence.
- **The projector deliberately reads only one of the two demo servers'
  databases.** Seeing orders placed at the *other* site converge into a
  book built from a single server's database is the concrete proof the
  mesh's "every server converges to the same history" promise holds — not
  a shortcut to avoid querying both.
- **Commands go through the real daemon IPC path, not a shortcut.**
  `SyncMesh.OrderBook.Api` plays the role of "the local app" — it calls
  `SyncMesh.Daemon.Ipc.LocalIpcClient` (already existed, marked in its own
  doc comment as "the reference client until a real one exists") against
  the correct site's named pipe, exercising the genuine Local App →
  Daemon → Server → Mesh path.
- **No trade matching, deliberately.** A real distributed matching engine
  needs strong consistency this mesh's design explicitly doesn't provide
  (see "Sync model & security baseline" below — full eventual
  replication, not consensus). Building one would contradict the
  architecture this whole project demonstrates, not showcase it. This
  domain only has `OrderPlaced`/`OrderCancelled` — an order book, not a
  trading engine — confirmed with the user as a scope decision before
  implementation, not assumed.
- **Synthetic traffic runs in-process, not through IPC.**
  `SyntheticOrderGenerator` calls `LocalEventWriter` directly (it's a
  `BackgroundService` inside the same daemon process that owns that
  writer) — no named-pipe round-trip needed, unlike the demo API's
  commands, which genuinely are a separate process.
- **`MarketDataOrderGenerator` is this project's first dependency on a
  live external network service** — see
  `docs/adr/0008-live-market-data-generator.md`. Same in-process
  `LocalEventWriter` write path as `SyntheticOrderGenerator`, just sourced
  from a real HTTP call instead of `Random`. Every failure mode (network
  down, timeout, rate-limited, invalid/unsupported symbol) degrades to
  "log a warning, skip this tick" — including the case where the
  provider's own rejection comes back as HTTP 200 with an error-shaped
  JSON body (`{"code":401,...}`) rather than a non-2xx status, which is
  checked for explicitly rather than inferred from the status code alone.
  Independent of `SyntheticOrderGenerator` — both default to enabled, and
  either can be turned off via config without touching the other.
- **`SyncMesh.AppHost`'s two-site topology is the first time server-mesh
  peering (`ServerMeshOptions.Peers`, proved in Phase 3's tests) runs in
  the live dev topology, not only Testcontainers.** Each site's tunnel
  ports (`TunnelAgentOptions.DirectListenPort`, `TunnelRelayOptions
  .AgentListenPort`/`ClientListenPort`) needed explicit distinct literal
  values for site B — unlike the NATS containers (which get dynamic,
  Aspire-managed ports via declared endpoints), the tunnel's plain TCP
  listeners aren't Aspire-managed endpoints, so two sites' daemon/server
  *processes* sharing one machine's port space would otherwise collide.
- **Known gap, not fixed here**: `SyncMesh.MeshMonitor.Api` only
  subscribes to site A's NATS hub — site B's daemon/server telemetry is
  on a fully separate NATS cluster (only the `ServerHost`↔`ServerHost`
  mesh peering bridges the two sites, and only for event replication, not
  general pub/sub) and never reaches it. Out of scope for this pass.
- **Test coverage is unit tests only, matching the `SyncMesh.MeshMonitor
  .Api` precedent** (which itself has no backend test project at all) —
  `OrderBookStore`'s fold logic is pure and cheap to test in isolation
  (`tests/SyncMesh.OrderBook.Tests`); no BDD/Testcontainers suite exists
  for this, a deliberate scope choice since this is a worked example, not
  a phase deliverable with entry/exit criteria.

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

**Ops/legal/compliance sign-off is out of scope for this PoC entirely** —
see [`PRODUCTION-HARDENING.md`](PRODUCTION-HARDENING.md) for the
consolidated list of what a real deployment would still need
(tunnel security review, retention compliance sign-off, real-scale
topology decisions, TLS/credential wiring). A POC ships against the smart
defaults and security baseline already decided in this document, not a
completed sign-off.

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
- **`docs/bdd/design/*.md` is the single source of truth for Gherkin**
  (per `CLAUDE.md`) — `docs/bdd/features/*.feature` is generated build
  output, gitignored, never hand-edited. See "Feature-doc extraction
  tooling" below for how and why. Because the generated source lives
  outside the project's directory tree,
  `ReqnrollUseIntermediateOutputPathForCodeBehind` must be `true`, or
  Reqnroll writes generated `.feature.cs` code-behind next to the linked
  source — i.e. into `docs/bdd/features/` — polluting a generated-output
  directory with more generated code.
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

## Feature-doc extraction tooling

See `docs/bdd/design/*.md` (per-feature design docs, each self-contained —
use case, sequence diagram, relevant C4 excerpt, deployment-model refs,
and a fenced ```gherkin``` block) and `tools/FeatureDocExtractor`.

- **Exactly one ```gherkin``` block per companion doc, no sub-splitting.**
  Gherkin only permits one `Feature:` block per `.feature` file, so the
  extractor requires exactly one match per doc: zero is a warning (doc
  still being drafted), more than one fails the build (ambiguous which
  block is authoritative). Output filename is always the doc's own
  filename stem + `.feature` — a 1:1 mapping, matching the pre-restructure
  layout.
- **Generated files carry a banner and are gitignored.** Every output file
  starts with `# GENERATED FILE — DO NOT EDIT DIRECTLY.` plus its source
  path. `docs/bdd/features/*.feature` is in `.gitignore` — committing both
  the Markdown source and its generated output would recreate exactly the
  staleness risk this tooling exists to eliminate, except worse (a stale
  *committed* file reads as authoritative). `git diff` reviewability isn't
  lost: the Gherkin text still lives in the `.md`'s fenced block either way.
- **Only writes when content actually changes.** The extractor compares
  against what's already on disk first — avoids needless file-mtime churn
  and incremental-build invalidation on every single build.
- **Must run during Restore, not a normal Build-pass Target.** This is the
  one genuinely non-obvious part: Reqnroll computes required per-item
  metadata (`CodeBehindFile`, `MessagesFile`, `NormalizedLogicalName`,
  etc.) on `ReqnrollFeatureFile` items via `<ItemGroup>` transforms that
  run once, at project-*evaluation* time, in its own imported `.targets`
  file — not inside any `<Target>`. A `<Target BeforeTargets="BeforeBuild">`
  that adds `ReqnrollFeatureFile` items at target-*execution* time (tried
  first) makes those items visible to later targets, but they never
  receive that metadata — Reqnroll's codegen then silently produces
  nothing usable and the test assembly ends up with zero discoverable
  tests, with no error to point at why. `dotnet build`/`dotnet test`
  always run an implicit `Restore` as a distinct, earlier MSBuild pass
  first; generating the `.feature` files during that pass
  (`Target Name="ExtractFeatureDocs" BeforeTargets="Restore"` in
  `SyncMesh.Bdd.Tests.csproj`) means they already exist on disk by the
  time the real Build pass evaluates the (ordinary, static)
  `ReqnrollFeatureFile` glob — exactly as if they were a checked-in file.
- **Invoked via `dotnet run --project`, not a resolved assembly path.**
  A `ProjectReference` to `tools/FeatureDocExtractor` with
  `OutputItemType` looks appealing but its metadata is only populated by
  `ResolveProjectReferences`, a target that runs *after* `BeforeBuild` —
  too late for a `BeforeTargets="Restore"` hook, which runs earlier still.
  `dotnet run --project` sidesteps the whole question by building the
  tool itself before running it.

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
  - **Follow-up observation**: on a later attempt (after adding the
    `mesh-monitor-api` resource), DCP hung *before creating any container
    at all* (`docker ps -a` stayed empty for 4+ minutes, not even
    reaching `created`) — worse than the original symptom above. Root
    cause both times traced to **orphaned `dcp` processes** left behind
    by previously killed `dotnet run --project src/SyncMesh.AppHost`
    attempts (killing the parent `dotnet` process directly doesn't clean
    up its child DCP process). Confirmed via an isolation test: the hang
    reproduced identically with `mesh-monitor-api` temporarily removed
    from `AppHost.cs`, ruling out that resource as the cause, and cleared
    immediately after killing every stray `dcp.exe` (`Get-Process | Where-
    Object { $_.ProcessName -match 'dcp' } | Stop-Process -Force`) before
    the next run. If a fresh `dotnet run --project src/SyncMesh.AppHost`
    hangs with zero containers appearing, check for and kill orphaned
    `dcp` processes first, before assuming the AppHost topology itself is
    broken.
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
  Markdown, not standalone `.puml` files — per explicit request. Per-feature
  diagrams (sequence diagrams, and any C4 component diagram owned by a
  single feature) live alongside that feature's design in
  `docs/bdd/design/*.md`, so each feature's design stands on its own —
  `docs/c4-diagrams.md` now holds only cross-cutting/not-yet-feature-owned
  C4 diagrams (System Context, Container Diagram, and the Mesh Monitor
  Dashboard's component diagram, pending a feature file of its own); the
  old `docs/sequence-diagrams.md` was retired entirely (2026-07-27) once
  every diagram it held moved into its owning companion doc(s). Salt for
  UI wireframes, in `docs/ui-wireframes.md`, layered like C4
  (context → container → component) rather than one flat mockup.
- **Doc set**: `docs/00-design-document.md` (architecture/goals/open
  questions) → `docs/05-implementation-guide.md` (static phased plan) →
  `docs/06-data-model.md` (envelope/entity/HLC shapes) →
  `docs/07-operations-guide.md` (ops-owned vs. dev-owned operational
  concerns) → `docs/08-deployment-models.md` (shared, cross-feature
  topology catalog) → **`docs/bdd/design/*.md`** (per-feature,
  self-contained design docs — use case, sequence diagram, relevant C4
  excerpt, deployment-model refs, and the Gherkin source of truth) →
  **`docs/bdd/features/*.feature`** (generated from `docs/bdd/design/*.md`
  via `tools/FeatureDocExtractor`, wired automatically into
  `SyncMesh.Bdd.Tests`'s build — never hand-edit) → `docs/adr/`
  (individual decisions). `WORKPLAN.md` tracks phase status against the
  implementation guide; this file tracks the conventions above.
