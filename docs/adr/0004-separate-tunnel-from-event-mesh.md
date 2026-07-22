# ADR-0004: Keep Remote Monitoring/Tunnel Architecturally Separate from the Event-Sync Mesh

| | |
|---|---|
| Status | Accepted |
| Date | 2026-07-22 |
| Deciders | Architecture |

## Context

Remote clients need two different capabilities against a recording
instance: (a) passive monitoring/telemetry, and (b) interactive tunnel
access (e.g. remote desktop / raw TCP / live control) for cases where
firewalls block direct connectivity, falling back to relay through the
nearest server. It's tempting to build both on top of the same NATS mesh
used for event sync, since the "relay via nearest server" idea is shared.

## Decision

- Passive monitoring/telemetry is published as ordinary NATS subjects
  (`monitor.<siteId>.<instanceId>.*`) on the same mesh used for event sync,
  since it is lightweight, low-volume, and benefits from existing
  interest-graph routing.
- Interactive tunnel access is built as a **separate** mechanism (reverse
  tunnel/relay tooling, e.g. self-hosted `frp`/`chisel`, or overlay
  networking such as WireGuard/Tailscale-style tooling), attempting direct
  connectivity first and falling back to relay through the nearest server
  only when direct access is blocked.
- These two concerns must have independent failure domains: an outage or
  bug in the tunnel/relay path must never affect event durability or sync,
  and vice versa.

## Considered Alternatives

- **Tunnel interactive sessions over NATS too** — technically possible
  (NATS can carry arbitrary byte streams), but conflates a
  low-volume/high-reliability requirement (events) with a
  high-volume/latency-sensitive, security-sensitive requirement
  (interactive sessions), and couples their failure domains unnecessarily.
- **Build both on a VPN overlay (WireGuard/Tailscale-style) exclusively** —
  works for the tunnel case, but is a poor fit for event sync's pub/sub,
  fan-out, and durable-buffer requirements; would mean maintaining two
  transports anyway, just organized differently.

## Consequences

- Positive: clean failure isolation; each mechanism can be reasoned about,
  scaled, and secured independently; monitoring stays cheap and simple by
  reusing the existing mesh rather than adding infrastructure.
- Negative: two mechanisms to operate instead of one; "relay via nearest
  server" logic must be implemented (and secured) twice, once per
  mechanism, rather than shared.
- Follow-up: a dedicated security review is required before implementing
  the interactive tunnel relay — session hijacking, auth, and attack
  surface at the relay point are not covered by this ADR and must not be
  assumed safe by default (see Open Questions in design doc).

## Related

`docs/00-design-document.md` §4.5, Open Question 5
