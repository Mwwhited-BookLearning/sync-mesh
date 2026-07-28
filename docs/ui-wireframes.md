# UI Wireframes

Salt wireframes for the Mesh Monitor Dashboard (`SyncMesh.MeshMonitor.Api` +
its planned SPA — see ADR-0005). This is the first UI work in the project,
per `CLAUDE.md`'s "Salt for UI wireframes if/when UI work starts."

Render with any PlantUML renderer that supports Salt (VS Code PlantUML
extension, plantuml.com server, or local `plantuml.jar`) — fenced
` ```plantuml ` blocks render directly out of Markdown, same convention as
`docs/c4-diagrams.md` and the per-feature diagrams in `docs/bdd/design/*.md`.

## Why these are layered, not one mockup

These wireframes deliberately mirror the C4 model's context → container →
component progression instead of jumping straight to one fully-detailed
screen:

1. **Layer 1 — Page layout.** Just the regions on the page and how they
   relate, no widget-level detail. Equivalent to a C4 Context diagram.
2. **Layer 2 — Topology Panel detail.** A zoomed-in look at *one* Layer-1
   region, with its own controls and content spelled out. Equivalent to a
   C4 Container diagram — one box from Layer 1, one level deeper.
3. **Layer 3 — Node Detail Panel detail.** A zoomed-in look at the *other*
   Layer-1 region, spelled out for each node kind it needs to render.
   Equivalent to a C4 Component diagram.

Each layer only adds detail to the region it's zooming into; it doesn't
restate the whole page. This keeps each diagram legible on its own and
keeps a later change (e.g. redesigning just the node detail panel) from
requiring the whole wireframe set to be redrawn.

## Layer 1 — Page Layout

```plantuml
@startsalt
{
  <b>Mesh Monitor Dashboard — Page Layout (coarsest level)
  ==
  {
    "Header / Nav region\n(mesh connection status, refresh indicator)"
  }
  {
    "Topology Panel region\n(see Layer 2)" | "Node Detail Panel region\n(see Layer 3)"
  }
  {
    "Status footer region\n(SignalR connection state, last-update timestamp)"
  }
}
@endsalt
```

- **Header/nav**: which hub(s) this dashboard instance is connected to
  (`MeshMonitorApiOptions.NatsUrls` — one per site in a multi-site mesh),
  and whether the SignalR connection is live.
- **Topology panel**: left region, the mesh-wide node/edge view — detailed
  in Layer 2.
- **Node detail panel**: right region, populated once a node is selected in
  the topology panel — detailed in Layer 3 (its content depends on whether
  a `DaemonStatus` or `ServerStatus` node is selected).
- **Footer**: connection health for the dashboard itself, not the mesh —
  distinguishes "the mesh has a problem" from "this browser tab lost its
  SignalR connection."

## Layer 2 — Topology Panel (zoom of Layer 1's left region)

```plantuml
@startsalt
{
  <b>Topology Panel — detail
  ==
  { {SI "Filter by siteId..."} | [ ] Daemons  | [X] Servers }
  ==
  {
    "   (o) site-a:daemon-1 --- (o) site-a:server-1 === (o) site-b:server-1   "
    "                                    \\--- (o) site-a:server-2            "
    "                                                                        "
    "   legend:  (o) healthy    (!) stale (no tick within 3x PublishInterval) "
    "            ---  leaf connection      ===  mesh peer connection         "
  }
}
@endsalt
```

- Node markers distinguish daemon vs. server (icon/shape, not just color —
  color alone isn't accessible).
- "Stale" is derived client-side from `LastSeenUtc` vs. a multiple of the
  publisher's own `PublishInterval` — the dashboard doesn't invent a new
  liveness signal, it just ages out what it already receives, consistent
  with monitoring being current-state-only (`ARCHITECTURE.md` → Passive
  monitoring).
- Edge kind (leaf vs. mesh peer) comes from which contract reported the
  relationship: a daemon reports its own nearest-server URL; a server
  reports `ConfiguredPeers`. The dashboard draws both from self-reported
  telemetry, never a separately maintained topology file — same principle
  ADR-0005 and the `ServerStatus` contract doc comment already state.

## Layer 3 — Node Detail Panel (zoom of Layer 1's right region)

Two variants, since `DaemonStatus` and `ServerStatus` don't share a shape.

### Daemon node selected

```plantuml
@startsalt
{
  <b>Node Detail — Daemon
  ==
  "Selected: site-a / daemon-1  (kind: daemon)"
  ==
  {#
  .                    | 
  SiteId               | site-a
  InstanceId           | daemon-1
  TimestampUtc         | 2026-07-27T18:04:02Z
  BufferedEventCount   | 42
  LeafConnected        | true
  }
}
@endsalt
```

### Server node selected

```plantuml
@startsalt
{
  <b>Node Detail — Server
  ==
  "Selected: site-b / server-1  (kind: server)"
  ==
  {#
  .                    | 
  SiteId               | site-b
  InstanceId           | server-1
  Url                  | nats://site-b-hub:4222
  EventsAppliedCount   | 18402
  }
  ==
  <b>Configured Peers
  {#
  PeerSiteId | PeerUrl                 | EventsForwardedCount
  site-a     | nats://site-a-hub:4222  | 9110
  site-c     | nats://site-c-hub:4222  | 421
  }
}
@endsalt
```

- Field lists here are a direct render of `SyncMesh.Contracts.DaemonStatus`
  / `ServerStatus` — the dashboard is not expected to derive or compute
  anything beyond what a node already self-reports.
- A standalone server (zero peers, §4.4) renders with an empty Configured
  Peers table, not a hidden section — absence of peers is itself meaningful
  state, not missing data.

## Status

Wireframe only — no frontend code exists yet (`web/mesh-monitor` is not
present in the repo). See ADR-0005 and `WORKPLAN.md` → "Mesh Monitor
Dashboard" for what's actually built versus planned.
