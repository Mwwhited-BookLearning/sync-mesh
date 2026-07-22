# CLAUDE.md — Project Guide for Claude Code

This file is the entry point for Claude Code when working in this repository.
Read this first, then `docs/00-design-document.md` for full context.

## What this project is

A distributed, event-sourced recording system with four tiers:

1. **Local App** — talks only to its local daemon (same-machine IPC).
2. **Local Daemon** — durable *only while recording*; buffers events and forwards
   them to the nearest server over an outbound-only connection.
3. **Server Mesh** — on-prem, WAN, and cloud servers exchange events with each
   other (peer-to-peer / peer-to-cloud) and converge to a consistent, ordered
   event history.
4. **Remote Monitoring** — remote clients observe/tunnel into a recording
   instance directly when possible, falling back to relay through the nearest
   server when firewalls block direct access.

Full rationale, diagrams, and decisions live in `docs/`. Do not re-litigate
settled architecture decisions (see `docs/adr/`) without flagging the
tradeoff explicitly to the human first.

## Tech stack conventions

- **Language**: C#, .NET (mix of .NET Core / modern .NET and .NET Framework
  4.8 where legacy interop is required — check the component's ADR before
  assuming which one).
- **Persistence**: Entity Framework Core. SQLite at the edge/daemon tier,
  PostgreSQL or SQL Server at the server tier. Schema must remain portable
  across all three providers — avoid provider-specific SQL in migrations
  unless isolated behind a provider check.
- **Transport**: NATS (Core NATS + JetStream), using leaf nodes between
  daemon ↔ nearest server, and gateway/supercluster connections between
  servers. See `docs/adr/0002-nats-leaf-nodes-for-transport.md`.
- **Ordering**: Hybrid Logical Clocks (HLC), not transport-level ordering.
  See `docs/adr/0003-hybrid-logical-clock-ordering.md`.
- **Legacy interop**: WCF services may sit at integration boundaries with
  older on-prem systems — isolate behind an anti-corruption layer, do not
  let WCF contracts leak into the core event model.
- **Diagrams**: PlantUML. C4 model for architecture diagrams
  (`docs/c4-diagrams/`), Salt for UI wireframes if/when UI work starts.
- **Behavior specs**: Gherkin/BDD feature files in `docs/bdd/features/`.
  Treat these as executable acceptance criteria — implement against them,
  don't just implement and retrofit a feature file afterward.
- **Docs**: Markdown throughout. Keep design docs and code in sync; if an
  implementation detail changes an ADR's assumption, update the ADR (append,
  don't silently rewrite history — mark superseded ADRs as such).

## How to work in this repo

1. Read `docs/00-design-document.md` end to end before touching Phase 1+ code.
2. Follow `docs/05-implementation-guide.md` phase by phase. Each phase lists
   its entry criteria, exit criteria, and the BDD feature files that must
   pass before moving on.
3. For any new architectural decision of consequence, write an ADR using
   `docs/templates/adr-template.md` rather than only leaving it in code
   comments or commit messages.
4. Idempotency and ordering are cross-cutting correctness requirements —
   every consumer of an event must be safe to receive it more than once and
   out of local-arrival-order. Do not write a consumer that assumes
   exactly-once or in-order delivery from the transport.
5. Keep the local daemon's durability scope narrow: it is a short-lived
   buffer for the recording session, not a long-term store. Do not let
   "just in case" thinking turn it into a second permanent event store.
6. Keep the monitoring/tunnel path and the event-sync path architecturally
   separate (different subjects/services, different failure domains) even
   though both may relay through the same nearest server.

## Open questions to flag to the human, not silently resolve

See "Open Questions & Risks" in `docs/00-design-document.md`. These require
a product/business decision, not just an engineering one.
