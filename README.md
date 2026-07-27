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

- `docs/c4-diagrams.md` — system context (C4 L1) and container (C4 L2)
  diagrams, plus any C4 component diagram not yet owned by a single
  feature (currently: Mesh Monitor Dashboard)
- `docs/bdd/design/*.md` — per-feature sequence diagrams and any C4
  component diagram owned by that feature, alongside its use case and
  Gherkin, so each feature's design stands on its own
- `docs/ui-wireframes.md` — Salt UI wireframes, layered like C4
  (context → container → component)
- `docs/08-deployment-models.md` — deployment topology shapes (client
  isolated, client→on-prem, client→cloud, standalone server, intra-site
  mesh with limited inter-site gateway, full mesh everywhere)

Each diagram is a fenced ` ```plantuml ` (or ` ```salt `) block inline in
the Markdown file — render with any PlantUML renderer (VS Code PlantUML
extension, plantuml.com server, or local `plantuml.jar`). C4 diagrams pull
the C4-PlantUML include from GitHub — vendor a local copy if you need
offline rendering.

## Architecture Decision Records

- `docs/adr/0001-event-store-on-ef-core.md`
- `docs/adr/0002-nats-leaf-nodes-for-transport.md`
- `docs/adr/0003-hybrid-logical-clock-ordering.md`
- `docs/adr/0004-separate-tunnel-from-event-mesh.md`
- `docs/adr/0005-mesh-monitor-dashboard.md`
- `docs/adr/0006-event-lineage-descriptive-provenance.md`
- `docs/templates/adr-template.md` — use for any new decisions

## BDD Feature Design Docs

Gherkin is authored in `docs/bdd/design/*.md` (one companion doc per
feature, each self-contained); `docs/bdd/features/*.feature` is
**generated** from these via `tools/FeatureDocExtractor` and must never be
hand-edited (gitignored build output — see `ARCHITECTURE.md` →
"Feature-doc extraction tooling").

- `docs/bdd/design/local-durability.md`
- `docs/bdd/design/event-ordering-and-idempotency.md`
- `docs/bdd/design/nearest-neighbor-sync.md`
- `docs/bdd/design/remote-monitoring-tunnel.md`
- `docs/templates/feature-design-template.md` — use for any new features

## Handing this off to Claude Code

Drop this whole folder into the root of your repository (or a `docs/`
subfolder plus `CLAUDE.md` at repo root — `CLAUDE.md` should stay at the
repo root for Claude Code to pick it up automatically). Then start with:

> Read CLAUDE.md and docs/00-design-document.md, then begin Phase 0 of
> docs/05-implementation-guide.md.
