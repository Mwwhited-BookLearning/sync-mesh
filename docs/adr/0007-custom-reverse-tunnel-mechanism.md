# ADR-0007: Custom Plain-TCP Reverse Tunnel for Interactive Access

| | |
|---|---|
| Status | Accepted |
| Date | 2026-07-27 |
| Deciders | Architecture |

## Context

ADR-0004 decided that interactive tunnel/relay access must be a mechanism
architecturally separate from the NATS event mesh, with an independent
failure domain, and named `frp`/`chisel`/a WireGuard-style overlay as
example candidates — without picking one. Phase 5 needs a concrete
mechanism: direct-connection-first with relay-fallback through the
nearest server when a remote client can't reach the daemon directly
(firewall/NAT).

## Decision

Build a small, self-contained, plain-TCP reverse tunnel directly in this
codebase — `SyncMesh.Daemon.Tunnel.TunnelAgent` (daemon side) and
`SyncMesh.ServerHost.Tunnel.TunnelRelay` (server side) — rather than
adopting an external tool or library. Concretely:

- **Direct listener**: the daemon's `TunnelAgent` listens on a configurable
  port; a remote client tries this first.
- **Relay path**: the daemon dials the nearest server's `TunnelRelay`
  **outbound only** (same "daemon dials out, nothing dials in" pattern as
  the NATS leaf node, ADR-0002) and keeps a persistent control connection
  open. On request, it opens a second outbound connection as the data
  channel. A remote client that can't reach the daemon directly connects
  to the relay instead, which pairs it with the requesting daemon's data
  channel and splices the two connections together.
- **Protocol-agnostic payload**: both paths forward raw bytes to a
  configurable local target endpoint — the same approach `frp`/`chisel`
  themselves use — so the tunnel never needs to know what's actually
  being tunneled (remote desktop, raw TCP, anything else per design doc
  §4.5).
- **One active session per daemon at a time** — a deliberate POC
  simplification, not a hard design limit. A second concurrent request is
  rejected immediately, on both the agent and relay side. True
  multiplexing (session IDs, N simultaneous data channels) is a natural,
  out-of-scope future enhancement.
- **`docs/06-data-model.md` §5's `tunnel.<siteId>.<instanceId>.control`
  subject carries `TunnelStatus`**: current-state telemetry only (whether
  the agent has a live relay connection, whether a session is active) —
  the same convention as `DaemonStatus`/`ServerStatus`. Real session-
  establishment signaling (the Hello/OpenDataChannel/etc. handshake) lives
  entirely inside the plain-TCP mechanism and never touches NATS.
- **TLS + service-credential authentication are explicitly deferred
  wholesale** to `PRODUCTION-HARDENING.md`, not implemented as part of
  choosing this mechanism. This phase ships the mechanism only, matching
  exactly how Phase 2/3 shipped the NATS leaf/gateway connections
  plaintext/unauthenticated by design.

## Considered Alternatives

- **`frp`** — a mature, widely-used reverse-tunnel/intranet-penetration
  tool; rejected as a new external binary/container dependency for what a
  small custom mechanism covers just as well for this POC, and it doesn't
  naturally integrate with this project's existing Testcontainers-based
  chaos-test conventions.
- **`chisel`** — similar reasoning to `frp`.
- **`FastTunnel.Core.Client` (NuGet)** — a real, published .NET reverse-
  tunnel library; rejected because it targets old TFMs (net5.0/
  netcoreapp3.1, this repo is net10.0-only) and is positioned as a full
  frp-style framework (subdomain routing, web admin UI) — more machinery
  than a POC tunnel needs.
- **Vendoring `kpreisser/TcpTunnel` from source** — no published NuGet
  package (source-only), and its 3-role Gateway/Proxy-Server/Proxy-Client
  model doesn't map cleanly onto this project's 2-tier daemon/server
  model without non-trivial adaptation.
- **VPN overlay (WireGuard/Tailscale-style)** — already considered and
  rejected in ADR-0004 for the same reasons (poor fit for event-sync's
  pub/sub needs, and this ADR only needed to pick the tunnel mechanism,
  not revisit that decision).

## Consequences

- **Positive**: small, fully owned, easy to test against real
  infrastructure (see `SyncMesh.Sync.Tests.TunnelFailureIsolationTests`);
  consistent with this project's established pattern of building simple
  custom point-to-point mechanisms over adopting off-the-shelf clustering/
  tunnel infra (see ADR-0002's Amendments for the same pattern applied to
  NATS leaf/gateway connections); zero new external dependency.
- **Negative / tradeoffs**: reinvents tunnel machinery mature tools
  already solve; no multiplexing (one session per daemon at a time); no
  TLS or credential auth yet — see `PRODUCTION-HARDENING.md`.
- **Follow-up**: TLS + service-credential auth (Phase 6/`PRODUCTION-HARDENING.md`);
  multiplexing, if concurrent sessions per daemon are ever actually
  needed.

## Related

`docs/00-design-document.md` §4.5, `docs/adr/0002-nats-leaf-nodes-for-transport.md`,
`docs/adr/0004-separate-tunnel-from-event-mesh.md`, `docs/06-data-model.md`
§5–§6, `docs/bdd/design/remote-monitoring-tunnel.md`, `PRODUCTION-HARDENING.md`.
