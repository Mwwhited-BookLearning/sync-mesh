# Distributed Event-Sourced Recording & Sync Mesh — Project Docs

This folder is meant to be dropped into (or used as) a repository handed
off to Claude Code for implementation.

## Start here

1. **`CLAUDE.md`** — project guide read automatically by Claude Code;
   conventions, stack, and working agreements.
2. **`docs/00-design-document.md`** — full design: goals, architecture
   tiers, non-functional requirements, decisions summary, open questions.
3. **`docs/06-data-model.md`** — event envelope, EF Core entities, HLC
   implementation sketch, NATS subject naming.
4. **`docs/05-implementation-guide.md`** — phased build plan with entry/exit
   criteria and linked BDD features per phase.
5. **`docs/07-operations-guide.md`** — ops-owned vs. dev-owned split for
   operational concerns (e.g. server-tier backup/retention).
6. **`WORKPLAN.md`** — living tracker for phase *status*: what's done,
   in-flight, next. Update this as you go; the implementation guide defines
   the plan, this tracks progress against it.
7. **`ARCHITECTURE.md`** — living record of engineering *conventions*
   adopted during implementation (framework choices, workarounds, things
   that didn't match the original docs), kept separate from phase status.

## Diagrams (PlantUML, embedded in Markdown)

- `docs/c4-diagrams.md` — system context (C4 L1), container (C4 L2), and
  daemon component (C4 L3) diagrams
- `docs/sequence-diagrams.md` — event recording flow, server mesh
  reconciliation, and remote monitoring/tunnel fallback flows

Each diagram is a fenced ` ```plantuml ` block inline in the Markdown file —
render with any PlantUML renderer (VS Code PlantUML extension, plantuml.com
server, or local `plantuml.jar`). C4 diagrams pull the C4-PlantUML include
from GitHub — vendor a local copy if you need offline rendering.

## Architecture Decision Records

- `docs/adr/0001-event-store-on-ef-core.md`
- `docs/adr/0002-nats-leaf-nodes-for-transport.md`
- `docs/adr/0003-hybrid-logical-clock-ordering.md`
- `docs/adr/0004-separate-tunnel-from-event-mesh.md`
- `docs/templates/adr-template.md` — use for any new decisions

## BDD Feature Files

- `docs/bdd/features/local-durability.feature`
- `docs/bdd/features/event-ordering-and-idempotency.feature`
- `docs/bdd/features/nearest-neighbor-sync.feature`
- `docs/bdd/features/remote-monitoring-tunnel.feature`
- `docs/templates/feature-template.feature` — use for any new features

## Handing this off to Claude Code

Drop this whole folder into the root of your repository (or a `docs/`
subfolder plus `CLAUDE.md` at repo root — `CLAUDE.md` should stay at the
repo root for Claude Code to pick it up automatically). Then start with:

> Read CLAUDE.md and docs/00-design-document.md, then begin Phase 0 of
> docs/05-implementation-guide.md.
