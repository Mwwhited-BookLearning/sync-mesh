# Production Hardening (Out of Scope for This PoC)

This repo is a **PoC/teaching example of meshed event sourcing** (HLC
ordering, idempotent apply, NATS leaf/gateway mesh, a direct-first/
relay-fallback tunnel) — not a path to a real production deployment. It
is not a CQRS example: there is no separate, denormalized read model
built from the sourced events (the daemon's read path queries the same
append-only event table the write path inserts into) — only describe
this project as CQRS if that changes.
Nothing in this file is expected to actually be built here; it exists so
every phase write-up and every ADR can point to **one place** for "what a
real deployment would still need," instead of each repeating its own
"not production ready" caveat (which is exactly what made the phase docs
hard to follow before this file existed — see `WORKPLAN.md`'s former
"Phase 6" section and `docs/00-design-document.md`'s former §8, both now
consolidated here).

Consolidates: `WORKPLAN.md`'s former "Phase 6 — Hardening & Operational
Readiness" checklist, `docs/00-design-document.md`'s former §8 "Open
Questions & Risks," and the scattered "deferred to Phase 6"/"gates
production" asides that used to be repeated in `ARCHITECTURE.md`,
ADR-0002, and ADR-0004.

## Transport security (TLS + service credentials)

- **NATS leaf/gateway connections** (daemon↔server, server↔server):
  plaintext and unauthenticated, by design, since Phase 2/3. TLS +
  registered service credentials is the decided *baseline* (see
  `docs/adr/0002-nats-leaf-nodes-for-transport.md`'s Amendment) — deciding
  the baseline is not the same as wiring it up, and wiring it up was
  explicitly out of scope for the POC phases.
- **Tunnel/relay connections** (daemon↔relay, remote client↔relay): plain
  TCP, unauthenticated, by design, since Phase 5. Same baseline decision
  applies (see `docs/adr/0004-separate-tunnel-from-event-mesh.md`'s
  Amendment and `docs/adr/0007-custom-reverse-tunnel-mechanism.md`).
- **`SyncMesh.MeshMonitor.Api`**: application-layer auth (bearer token +
  ticket exchange, see `docs/adr/0009-ticket-based-signalr-auth.md`) is
  now built, but the transport itself is still plain HTTP/WebSocket —
  both the bearer token (once, at ticket issuance) and the ticket value
  cross the wire in cleartext without TLS. TLS termination for this
  dashboard is still this section's concern, not resolved by ADR-0009.
  **This is no longer just a confidentiality gap** — confirmed via a
  real-browser check (ADR-0009's Amendment): the frontend's ticket-hash
  computation uses `crypto.subtle`, which browsers refuse to expose
  outside a secure context (HTTPS, or the literal hostname `localhost`).
  Reached by any other hostname/IP without TLS, the dashboard's SignalR
  connection **fails outright**, not just insecurely — TLS here is a
  functional requirement for any real (non-localhost) deployment, not
  only a hardening nice-to-have.
- **What a real deployment would still need**: `SslStream`/TLS
  termination on every connection listed above; certificate provisioning
  and rotation; a bearer-token or mTLS service-credential handshake
  scoped to the daemon/server instance (never end-user identity); a
  decision on certificate-authority trust (self-signed is fine for a POC,
  not for production).

## Tunnel/relay security review

Session hijacking risk, full attack-surface analysis, and the
remote-user-authorization layer on top of the service-credential baseline
(see `docs/adr/0004-separate-tunnel-from-event-mesh.md`) — none of this
has ever been performed, and it isn't planned for this repo. A real
deployment needs a dedicated security review before exposing the tunnel
path to anything beyond a trusted network.

## Retention / backup / compliance sign-off

Smart defaults exist — see `docs/07-operations-guide.md` → "Retention
default" (7 years for adult records, a longer distinct default for
minors, following common U.S. healthcare-record retention practice) — but
no legal/compliance sign-off on the exact figures for any real
jurisdiction or accreditation was ever sought, and no RPO/RTO targets were
set. A smart default is a starting point, not that sign-off. Backup/
restore mechanics themselves are ops-owned (standard, transparent
per-provider tooling), per `ARCHITECTURE.md` → "Operational vs.
development ownership."

## Chaos / load testing at realistic scale

Single-scenario proofs exist — an explicit extended-disconnect/reconnect
test (`SyncMesh.Sync.Tests.DaemonToServerSyncTests`) and the tunnel
failure-isolation tests (`SyncMesh.Sync.Tests.TunnelFailureIsolationTests`)
— but no load testing under realistic outage durations/volumes was
performed or is planned. There are known reports of gaps in NATS leaf-node
mirror sync after extended disconnection generally; this repo's single
proof should not be read as a guarantee against your own outage patterns.

## Full-mesh vs. hub-and-spoke at real scale

Topology is fully flexible and config-driven with no architectural
minimum or maximum on server/site/gateway count (see design doc §4.4 and
`docs/08-deployment-models.md`) — but how many designated gateway servers
per inter-site link a *real* deployment should use (one vs. a small
redundant set for HA) was never decided, because there's no real
deployment to decide it for. Revisit once actual site count and
instability characteristics are known.

## Offline/batch reconciliation for standalone sites

A standalone (zero-peer) server is a first-class, permanent deployment
mode (see design doc §4.4) — but the offline/batch mechanism a standalone
site would use to *later* reconcile with others, if it ever needed to, is
undesigned. Idempotent apply and HLC ordering don't depend on how an
event arrives, so this is compatible without redesign — it's simply a
distinct future decision, not assumed to be "just NATS gateways, later."

## WCF/legacy interop

Resolved, not deferred: out of scope for this project entirely. If some
future external component needs WCF integration with an older on-prem
system, that integration is implemented within that component — isolated
behind an anti-corruption layer — never inside sync-mesh itself.
