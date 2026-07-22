# Operations Guide

Operational patterns and practices for running the server mesh — things a
platform/ops owner configures and executes, as distinct from behavior the
application itself must implement. The split matters: default to standard,
external, transparent tooling wherever the concern can be fully handled
outside the application; only pull something into development/design scope
when it genuinely cannot be isolated that way (the app's own correctness
guarantees depend on it, or no external tool can see enough to do it safely).

This doc grows alongside Phase 6 (`docs/05-implementation-guide.md` /
`WORKPLAN.md`) as operational concerns are worked through. Today it covers
server-tier retention & backup (design doc Open Question 3); more sections
land as other operational questions are settled.

## Server-Tier Retention & Backup

### Ops-owned (suggested patterns)

Standard database backup/retention tooling applies per provider — this is
infrastructure configuration, not application code:

- **PostgreSQL**: continuous archiving + PITR (`pg_basebackup` + WAL
  archiving, or a managed equivalent — e.g. cloud-provider automated
  backups/snapshots). Retention window and RPO/RTO targets are an
  ops/business decision, not something the event-store schema needs to know
  about.
- **SQL Server**: full + differential + transaction log backup schedule
  (native `BACKUP DATABASE`/`BACKUP LOG`, or a managed equivalent). Same
  retention/RPO/RTO framing as above.
- Storage lifecycle (how long backups themselves are retained, cold/archive
  tiering, offsite/cross-region copies) is standard ops practice — configure
  via the backup tooling/cloud provider, not the application.
- None of this requires the application to be aware it's happening. A
  restore should be transparent to `EventStoreDbContext` — it's still just a
  PostgreSQL/SQL Server database with the same schema.

**Still open (needs an ops/business decision, tracked in
`docs/00-design-document.md` §8, Open Question 3):** actual retention
duration, RPO/RTO targets, and archival strategy. Not a development decision
— flag to the human, don't default silently.

### Development-owned (cannot be transparently externalized)

The one piece ops backup/restore tooling *can't* see: whether it's safe to
ever **purge** (not just back up) old rows from the live `Events` table
without breaking the mesh's own correctness guarantees. Two concrete
constraints any purge/archival feature must respect:

- **Idempotent-apply safety** (`docs/06-data-model.md` §4): dedupe works by
  checking `GlobalEventId` against what's already stored. If a very late
  at-least-once redelivery arrives *after* its row has been purged, the
  dedupe check no longer finds it and the event gets re-applied — a
  correctness bug, not just a storage/ops concern. Any purge policy needs
  either a tombstone/watermark the apply path can still consult, or a
  provable bound on maximum redelivery lateness before purging past it.
- **Replay ordering** (`docs/06-data-model.md` §3, ADR-0003): if replay ever
  needs to reconstruct history from a given HLC watermark forward, purged
  rows must not leave gaps that make that reconstruction ambiguous.

If and when a purge/archival feature is actually needed (as opposed to
"keep everything, let ops back it up"), design it here, with an ADR, before
implementation — this is exactly the kind of correctness-adjacent decision
`CLAUDE.md` says not to silently resolve.
