# ADR-0006: Event Lineage as a Descriptive-Only Provenance Graph

| | |
|---|---|
| Status | Accepted |
| Date | 2026-07-27 |
| Deciders | Architecture |

## Context

An event should be able to record which prior event(s) its data was
derived or sourced from — e.g. a computed/aggregated event referencing the
raw events it summarizes, or a corrected event referencing what it
supersedes. This is a genuine many-to-many relationship: an event can have
multiple parents (derived from more than one source) and multiple children
(more than one downstream event can be derived from it).

This must be purely descriptive/audit metadata. It must never become a
second ordering or dependency mechanism alongside HLC ordering and
idempotent apply (`docs/06-data-model.md` §3–4) — CLAUDE.md's working
agreement #4 requires every consumer to stay safe under out-of-order,
at-least-once delivery, and this must not create a new way to violate that.

## Decision

Add an additive `EventLineage(ChildEventId, ParentEventId)` entity/table:
genuine many-to-many via a composite primary key, no navigation properties
on `EventRecord`, and — the key decision — **no database-enforced foreign
key** from either column to `EventRecord.GlobalEventId`.

`EventEnvelope` gains an optional `ParentEventIds` field, populated by
`LocalEventWriter` at authorship time and carried through the wire
envelope unchanged; `ApplyResponder` inserts the corresponding
`EventLineage` rows alongside the `EventRecord` insert, using the exact
same idempotency gate (skipped on the duplicate/no-op path, exactly like
`EventRecord` itself).

## Considered Alternatives

- **Database-enforced foreign key to `EventRecord`** — rejected. Because
  `ApplyResponder` is a shared endpoint for both daemon-forwarded and
  peer-gossiped events with no cross-node ordering guarantee, a child
  event can legitimately be applied at some server before its parent
  arrives via a different gossip path. A hard FK checked synchronously at
  insert time would reject that child's apply purely due to a benign
  timing race — turning descriptive metadata into an accidental ordering
  dependency, which is exactly what this decision must avoid.
- **Navigation properties on `EventRecord`** — rejected. `EventRecord` is
  deliberately flat with zero navigation properties today; adding one for
  a purely descriptive, optional relationship would break that established
  terseness for no behavioral benefit (no query in this codebase currently
  needs to join through it eagerly).
- **Store `ParentEventIds` only inside `PayloadJson`, no dedicated table**
  — rejected. Makes lineage un-queryable relationally (no "find all
  children of event X" query without deserializing every payload),
  defeating the audit/traceability purpose this exists for.

## Consequences

- **Positive**: additive and backward-compatible — no changes to
  `EventRecord`, no new migration risk to existing data, no change to
  idempotent-apply or HLC-ordering semantics. `ApplyResponder`'s mesh
  relay path needed zero changes, since it forwards the raw serialized
  envelope bytes verbatim and `ParentEventIds` is just another field on
  that same JSON payload.
- **Negative / tradeoffs**: lineage referential integrity is only as good
  as authorship-time validation (`LocalEventWriter` checks parents exist
  in *that daemon's own* local store — see `docs/06-data-model.md` §7). A
  dangling `ParentEventId` is possible in principle (e.g. if a daemon's
  local store were ever purged independently in the future — not
  currently planned) and must be tolerated by any future lineage-reading
  tooling; this is an accepted risk, not a gap to close now.
- **Follow-up**: surfacing `ParentEventIds` on the daemon's buffered-read
  path (`ReadEventsRequest`/`RecordedEvent`) is a natural next step, not
  required for the schema/wire contract to be fully populated end-to-end.

## Related

`docs/06-data-model.md` §7, `WORKPLAN.md` → "Event Lineage (Provenance)",
`ARCHITECTURE.md` → "Event lineage (provenance)", CLAUDE.md working
agreement #4 (idempotency/ordering).
