# CLAUDE.md — Project Guide for Claude Code

This file is the entry point for Claude Code when working in this repository.
Read this first, then `docs/00-design-document.md` for full context. Before
starting new work, also check `WORKPLAN.md` (current phase status) and
`ARCHITECTURE.md` (engineering conventions already established) so you don't
re-decide something that's already settled.

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

- **Language**: C#, modern .NET only (`net10.0` — see `ARCHITECTURE.md`).
  No .NET Framework 4.8 components: that contingency was only for WCF/legacy
  interop, which is out of scope for this project (see below and design doc
  §8, Open Question 6, resolved).
- **Persistence**: Entity Framework Core. SQLite at the edge/daemon tier,
  PostgreSQL or SQL Server at the server tier. Schema must remain portable
  across all three providers — avoid provider-specific SQL in migrations
  unless isolated behind a provider check.
- **Transport**: NATS (Core NATS + JetStream), using leaf nodes between
  daemon ↔ nearest server, and gateway/supercluster connections between
  servers. See `docs/adr/0002-nats-leaf-nodes-for-transport.md`.
- **Ordering**: Hybrid Logical Clocks (HLC), not transport-level ordering.
  See `docs/adr/0003-hybrid-logical-clock-ordering.md`.
- **Legacy interop**: out of scope for this project. If some future
  external component needs WCF integration with an older on-prem system,
  that integration is implemented within that component — isolated behind
  an anti-corruption layer — not built into sync-mesh. WCF contracts must
  never leak into this project's core event model.
- **Diagrams**: PlantUML, embedded as fenced ` ```plantuml ` blocks directly
  in Markdown (not standalone `.puml` files). C4 model for architecture
  diagrams (`docs/c4-diagrams.md`), Salt for UI wireframes if/when UI work
  starts.
- **Behavior specs**: Gherkin/BDD feature files in `docs/bdd/features/`.
  Treat these as executable acceptance criteria — implement against them,
  don't just implement and retrofit a feature file afterward.
- **Docs**: Markdown throughout. Keep design docs and code in sync; if an
  implementation detail changes an ADR's assumption, update the ADR (append,
  don't silently rewrite history — mark superseded ADRs as such). Doc set:
  `docs/00-design-document.md` (architecture/goals/open questions) →
  `docs/05-implementation-guide.md` (static phased plan) →
  `docs/06-data-model.md` (envelope/entity/HLC shapes) →
  `docs/07-operations-guide.md` (ops-owned vs. dev-owned operational
  concerns, e.g. backup/retention) → `docs/adr/` (individual decisions) →
  `docs/bdd/features/` (executable acceptance criteria). `WORKPLAN.md`
  tracks phase status against the implementation guide; `ARCHITECTURE.md`
  tracks engineering conventions established along the way.
- **Operational vs. development ownership**: when a concern (backup
  schedules, retention windows, infra sizing, etc.) can be fully handled by
  standard external/transparent tooling, document the suggested pattern in
  `docs/07-operations-guide.md` rather than building it into the
  application. Only pull something into development/design scope when it
  genuinely can't be externally isolated — e.g. the app's own correctness
  guarantees (idempotent apply, replay ordering) depend on it.
- **Configuration**: any tunable value (buffer caps, timeouts, retention,
  reconnect/backoff settings, subject prefixes, etc.) must be configurable
  via the `Microsoft.Extensions.Options` pattern — bind a POCO options class
  from configuration and consume it as `IOptions<T>` /
  `IOptionsMonitor<T>` (the latter where a value may need to change without a
  restart), never read raw config values inline via `IConfiguration["..."]`
  scattered through application code. Every options class must have smart
  defaults set on its properties so the app runs sensibly out of the box with
  zero configuration — configuration overrides the default, it isn't
  required to supply one. Register with
  `services.AddOptions<T>().Bind(...).ValidateDataAnnotations()` (or a custom
  `IValidateOptions<T>`) so invalid configuration fails fast at startup, not
  deep in a code path. This applies solution-wide, not just to the daemon's
  buffer cap (Open Question 1 in the design doc) — see `WORKPLAN.md` for
  which specific values are still open questions vs. already defaulted.

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
7. Keep `WORKPLAN.md` and `ARCHITECTURE.md` current as work progresses.
   `WORKPLAN.md` tracks phase *status* — what's done, in-flight, next —
   against the static plan in the implementation guide. `ARCHITECTURE.md`
   tracks engineering *conventions* adopted along the way (framework
   choices, workarounds, things that didn't match the original docs) so
   they stay consistent across phases instead of being re-decided each
   time. Status goes in the former, durable patterns in the latter — don't
   mix the two.

## Open questions to flag to the human, not silently resolve

See "Open Questions & Risks" in `docs/00-design-document.md`. These require
a product/business decision, not just an engineering one.
