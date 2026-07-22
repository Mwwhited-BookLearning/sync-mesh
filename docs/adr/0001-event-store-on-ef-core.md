# ADR-0001: Event Store Built on EF Core, Portable Across SQLite/PostgreSQL/SQL Server

| | |
|---|---|
| Status | Accepted |
| Date | 2026-07-22 |
| Deciders | Architecture |

## Context

We need a durable event store usable at both the edge (local daemon,
SQLite) and the server tier (PostgreSQL or SQL Server), sharing one schema
and one application-layer implementation. No existing off-the-shelf .NET
library provides a mature, actively maintained event store spanning all
three providers plus mesh-sync support out of the box.

## Decision

Build a minimal, purpose-built event store on top of EF Core: a single
append-only `Events` table (table-per-hierarchy style), with provider
selection handled entirely at the DI/configuration layer. Do not adopt a
third-party event-sourcing framework for the storage layer.

## Considered Alternatives

- **Finaps.EventSourcing.EF** — EF Core based, but only supports SQL Server
  and PostgreSQL; no SQLite support, which we need at the edge.
- **EventStoreDB** — purpose-built event store with strong ordering/
  subscription primitives, but introduces a separate specialized database
  product at every tier including the edge daemon, which conflicts with
  the "lightweight, embeddable at the edge" requirement.
- **Marten** — excellent EF-adjacent event store, but PostgreSQL-only,
  which rules out our SQL Server and SQLite requirements.
- **Roll our own on EF Core** (chosen) — the storage need is simple enough
  (append-only table, optimistic concurrency via unique index) that a
  hand-rolled implementation is lower risk than adopting a partially-fitting
  framework, and keeps us free to evolve the schema for HLC-based ordering
  and mesh sync without fighting a framework's assumptions.

## Consequences

- Positive: full control over schema evolution, no dependency on a
  framework's release cadence for cross-provider bugs, simplest possible
  mental model for the team.
- Negative: we own concerns a mature framework would otherwise provide —
  snapshotting strategy, migration tooling ergonomics, and any
  framework-level tooling (dashboards, etc.) must be built or done without.
- Follow-up: define a snapshotting strategy once stream lengths are
  understood in practice (not needed for MVP).

## Related

`docs/06-data-model.md`, `docs/00-design-document.md` §5
