# ADR-0005: Mesh Monitor Dashboard — a Separate, Read-Only, Mesh-Wide Ops View

| | |
|---|---|
| Status | Accepted (see Amendment) |
| Date | 2026-07-27 |
| Deciders | Architecture |

## Context

See `docs/00-design-document.md` §4.6 for the concept and how it differs
from §4.5's per-instance remote monitoring. In short: an operator running
several daemons/servers wants one live view of the whole mesh's topology at
once, not a session into a single recording instance.

Work on this started (`SyncMesh.MeshMonitor.Api`, plus `ServerStatus` /
`ServerMonitorPublisher` additions to the server tier) without a design doc
section, an ADR, or a `WORKPLAN.md` entry — this ADR retroactively documents
the decision that implementation already assumes, and flags the parts of it
that were never actually confirmed as a deliberate architectural choice.

## Decision

Build a small, dedicated, **read-only** web app (`SyncMesh.MeshMonitor.Api`)
whose only job is to visualize mesh state:

- Subscribes once to the wildcard subject `monitor.>` on the hub-side NATS
  connection — the exact telemetry Phase 4 already publishes
  (`DaemonStatus` / `ServerStatus`); no new subject namespace, no new wire
  format.
- Holds an **in-memory, non-durable** topology snapshot (`ITopologyStore`).
  There is nothing to persist: like the telemetry itself (see
  `ARCHITECTURE.md` → Passive monitoring), a missed tick is superseded by
  the next one, so a dashboard restart just re-learns the topology from
  ongoing publishes — no different in kind from `MonitorPublisher`'s own
  "nothing to replay" reasoning.
- Exposes that snapshot two ways: `GET /api/topology` (a freshly opened
  browser tab's first paint) and a SignalR push (`NodeUpdated`, for tabs
  already open).
- Serves a browser single-page app as static files from the same host
  (`wwwroot`, `MapFallbackToFile("index.html")`) — **not yet built**, see
  Consequences.

This is deliberately scoped to observation only. It does not participate in
the interactive tunnel (§4.5 / ADR-0004) and grants no control over any
instance, so it does not need that feature's dedicated security review to
provide value — but it does need its own access control before it can be
reachable outside a fully trusted network, which it does not have yet (see
Consequences).

## Considered Alternatives

- **Extend `SyncMesh.MonitorClient`** (the existing console client from
  Phase 4) — rejected for this purpose: a CLI that prints one status line
  per tick can't give a human an at-a-glance multi-node topology graph.
  Kept as-is for scripted/headless checks; the two tools now serve different
  audiences rather than one superseding the other (see Follow-up).
- **Off-the-shelf observability stack** (e.g. a NATS-subject-to-Prometheus
  exporter feeding Grafana) — **not rejected, genuinely undecided.** This is
  exactly the shape of concern `ARCHITECTURE.md` → "Operational vs.
  development ownership" says should default to standard external tooling
  rather than a bespoke build, unless the app's own correctness guarantees
  depend on it (they don't here — this is pure observability). Building a
  bespoke SignalR dashboard was started without weighing this tradeoff
  against the human first, which is what this ADR is flagging now rather
  than silently ratifying after the fact.
- **Fold this into `SyncMesh.ServerHost`** — rejected: would couple an
  operator-facing viewing concern to the sync service's own process and
  failure domain, the same separation principle CLAUDE.md's working
  agreement #6 already applies to keeping monitoring and event-sync apart.

## Consequences

- **Positive**: zero new telemetry format or subjects — pure reuse of Phase
  4's `monitor.*` publishing; independent process and failure domain (this
  dashboard crashing or restarting affects nothing else in the mesh).
- **Negative / tradeoffs**:
  - Ships today as a backend with **no frontend** — `web/mesh-monitor`
    (referenced in `Program.cs`'s comments) does not exist, so nothing is
    served past the raw JSON API.
  - **No authentication** on `/api/topology` or the SignalR hub — every
    other cross-instance connection in this project (leaf, gateway, tunnel)
    has a decided TLS + registered-service-credential baseline
    (`ARCHITECTURE.md` → Sync model & security baseline); this dashboard
    currently has none and must not be exposed outside a fully trusted
    network until it does.
  - No test project exists for `SyncMesh.MeshMonitor.Api`, unlike every
    other component in this solution.
  - No BDD feature file exists for this — contrary to `CLAUDE.md`'s
    "implement against feature files, don't retrofit" convention.
- **Follow-up work**:
  1. Resolve the off-the-shelf-tooling question above with the human before
     investing further in the bespoke frontend.
  2. Build the SPA (if the answer to #1 is "keep it bespoke") or drop the
     static-file-serving scaffolding (if not).
  3. Add authentication consistent with the rest of the mesh's baseline.
  4. Add a test project.
  5. Reconcile this work into `WORKPLAN.md` as a tracked item rather than
     leaving it outside the phase plan (done alongside this ADR).

## Amendment (2026-07-27) — bespoke dashboard confirmed, frontend built

The off-the-shelf-tooling question in Considered Alternatives is now
resolved: **confirmed with the human — keep the bespoke dashboard**, not a
Grafana/exporter swap. The frontend (`web/mesh-monitor`, Vue 3 + Element
Plus + vis-network topology graph + Pinia-as-ViewModel) has since been
built, with 10 Vitest unit tests + 1 Playwright e2e smoke test, all
passing, and is served from `SyncMesh.MeshMonitor.Api`'s own `wwwroot`
(populated automatically on `dotnet build`, not just `publish` — see
`UI-ARCHITECTURE.md`). Status moves from Proposed to Accepted on that
basis — this ADR's core mechanism (in-memory topology store, SignalR
push, REST snapshot, telemetry-only, no new subjects) stands as built,
not just as designed.

**Resolved by a later pass (2026-07-28)**:
- **Authentication added**: both `/api/topology` and `MeshMonitorHub` now
  require either a bearer token or a redeemed ticket — see
  [ADR-0009](docs/adr/0009-ticket-based-signalr-auth.md) for the ticket-
  exchange design (built specifically so a bearer token never has to
  appear in the SignalR connection URL). TLS is still not part of this —
  that remains tracked in `PRODUCTION-HARDENING.md`, unaffected.

**Resolved by a later pass (2026-07-27)**:
- **Dual-hub telemetry gap fixed**: `MeshMonitorApiOptions.NatsUrl`
  (single string) became `NatsUrls` (a list), and `MonitorSubscriber` now
  runs one independent subscribe loop per configured URL (mirroring
  `MeshForwarder`'s one-loop-per-peer shape), so this dashboard now
  covers every site's hub in the two-site AppHost topology, not just
  site A's. See `ARCHITECTURE.md` → "Mesh-wide monitoring dashboard and
  deployment-model sandbox".
- **Backend test project added**: `tests/SyncMesh.MeshMonitor.Tests`
  (xUnit) — `TopologyStoreTests` (upsert/snapshot fold logic) and
  `MonitorSubscriberParsingTests` (the `DaemonStatus`/`ServerStatus`
  discriminated-union parsing, via `InternalsVisibleTo` on `ParseNode`).
  9 tests, matching the Order Book demo's unit-tests-only precedent (see
  below).
- **No BDD feature file — resolved as a deliberate scope decision, not a
  gap**: like the Order Book demo, this is developer tooling built
  alongside the phases, not a phase deliverable with its own entry/exit
  criteria in `docs/05-implementation-guide.md`. Unit tests covering the
  fold/parsing logic are the right-sized coverage here; a full
  Gherkin/Reqnroll suite would be scope creep for a dashboard whose
  actual correctness guarantee (telemetry in, topology snapshot out) unit
  tests already exercise directly.
- **Visually/functionally confirmed live**: the two-site AppHost topology
  now starts cleanly end to end (see ADR-0001's Amendment for why the
  server tier runs on SQLite in this dev topology, which is what actually
  unblocked this) and this dashboard's `/api/topology` reflects real
  telemetry from both sites once running.

## Related

`docs/00-design-document.md` §4.5, `docs/adr/0004-separate-tunnel-from-event-mesh.md`,
`docs/c4-diagrams.md` (Container diagram update + new Component diagram),
`docs/ui-wireframes.md`, `UI-ARCHITECTURE.md` (frontend conventions),
`WORKPLAN.md` → "Developer tooling built alongside the phases",
`ARCHITECTURE.md` → "Mesh-wide monitoring dashboard and deployment-model
sandbox"
