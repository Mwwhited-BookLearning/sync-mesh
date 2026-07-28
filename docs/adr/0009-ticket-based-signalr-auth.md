# ADR-0009: Ticket Exchange Instead of a Bearer Token in the SignalR URL

| | |
|---|---|
| Status | Accepted |
| Date | 2026-07-28 |
| Deciders | Architecture |

## Context

`SyncMesh.MeshMonitor.Api` had no authentication at all (ADR-0005's
Follow-up item 3, deferred to `PRODUCTION-HARDENING.md`). Adding it means
protecting both a plain REST endpoint (`GET /api/topology`) and a SignalR
hub (`MeshMonitorHub`). The standard way to authenticate a SignalR
connection from a browser is a bearer token in the connection URL's query
string (`?access_token=...`) — a browser's WebSocket handshake can't set
a custom `Authorization` header at all, so ASP.NET Core's own JWT Bearer
handler has a documented `OnMessageReceived` hook specifically to pull the
token from there instead.

That pattern puts a long-lived, high-value credential somewhere it's
routinely logged: web server access logs, reverse-proxy logs, browser
history. Explicitly asked to avoid that for this feature.

This project doesn't issue bearer tokens itself — the decision here
assumes some external issuer already exists and hands the operator a JWT
by whatever means; this ADR only covers how that token is used *and* how
it stops needing to appear in a URL at all.

## Decision

A short-lived, single-use **ticket exchange**, layered on top of standard
JWT Bearer auth rather than replacing it:

1. `POST /auth/ticket` — requires a real bearer token via the
   `Authorization` header (never a ticket; see Consequences). Body:
   `{ "oneTimeSecret": "<client-generated random string, >=16 chars>" }`.
   The server validates the bearer token as normal, generates an
   unguessable `ticketId` (128-bit random), computes
   `hashedTicket = HMAC-SHA256(key: oneTimeSecret, message: ticketId)`,
   stores `hashedTicket -> (caller's ClaimsPrincipal, expiresAtUtc)`
   in-memory, and returns **only the raw `ticketId`** — never the hash,
   and never anything reusable as a credential by itself.
2. The client, holding both `ticketId` and its own `oneTimeSecret`,
   independently computes the same `hashedTicket` value and presents
   *that* — not the raw `ticketId`, not the bearer token — on the actual
   request that needs it: `?ticket=<hashedTicket>` in the URL (the
   SignalR/WebSocket case this exists for), or an
   `Authorization: Ticket <hashedTicket>` header for any other caller
   that can set headers but still wants to avoid even this short-lived
   value being in its own logs.
3. A custom ASP.NET Core authentication scheme (`TicketAuthenticationHandler`,
   scheme name `"Ticket"`) looks up `hashedTicket`, and if found and
   unexpired, **redeems it exactly once** (removed from the store on
   lookup, valid or not — the ticket is one-time-use by construction, not
   by convention) and authenticates the request as the original bearer
   token's identity. Both `/api/topology` and `MeshMonitorHub` accept
   either scheme (`"Bearer,Ticket"`).

The server never transmits the actual bearer-equivalent value
(`hashedTicket`) in any response body — it's independently derived by
both sides from information each already holds. If a URL carrying
`hashedTicket` leaks via a log, an attacker gets a value that's already
single-use-consumed by the legitimate client (which redeems it within
moments of receiving `ticketId`), not a standing credential.

## Considered Alternatives

- **Bearer token directly in the SignalR URL** (the standard/documented
  ASP.NET Core pattern) — rejected: exactly the exposure this ADR exists
  to avoid.
- **Cookie-based auth for the SignalR connection** — would avoid the URL
  entirely, but introduces CSRF considerations and doesn't generalize to
  non-browser callers the same way; also a bigger behavioral change from
  "just validate a bearer token" than a ticket exchange.
- **Ticket store as a signed, stateless token (e.g. a second short-lived
  JWT) instead of server-side state** — would avoid the in-memory store,
  but a self-verifying ticket that's valid on its own defeats the
  single-use property: nothing server-side would need to be consulted to
  detect replay. The in-memory store (same "nothing to persist" reasoning
  as `ITopologyStore`/`IOrderBookStore`) is what makes "redeemed exactly
  once" an actual guarantee, not just a client-side convention.
- **Hash the ticket server-side instead of trusting the client to
  recompute it** — considered, but then the server would have to return
  the hash directly (defeating the point: the response body would then
  itself carry the actual bearer-equivalent value), or keep the raw
  secret server-side long enough to compute it later (worse — now the
  secret is state that must be protected, not just a client-held value
  used once, synchronously, at issuance time).

## Consequences

- Positive: the real bearer token appears only in an `Authorization`
  header, sent once, to mint a ticket — never in a URL, never logged by
  anything that logs URLs. The value that *does* end up in a URL is
  short-lived (`TicketOptions.Ttl`, default 60s), single-use, and useless
  to a store-leak on its own (the store contains only hashes, not the
  ticketId/secret pair needed to forge one).
- Negative / tradeoffs: an extra round trip (`POST /auth/ticket` before
  the real connection) versus just putting the token in the URL directly;
  in-memory-only ticket state means a dashboard restart invalidates any
  outstanding (unredeemed) tickets — acceptable given their ~60s
  lifetime, same reasoning as every other in-memory store in this
  project.
- `POST /auth/ticket` itself only accepts the `"Bearer"` scheme, not
  `"Ticket"` — deliberately: a ticket must not be usable to mint another
  ticket indefinitely, only a real bearer token grants that.
- This project still does not issue bearer tokens — `MeshMonitorAuthOptions
  .SigningKey` must be configured (no default; a baked-in default would
  be a real vulnerability, not a convenience) to match whatever external
  issuer signs the tokens operators are handed.
- Follow-up: none identified specific to this ADR. The broader "no TLS"
  concern for this dashboard (bearer tokens and tickets both still cross
  the wire in cleartext without TLS) remains tracked in
  `PRODUCTION-HARDENING.md`, unaffected by this change.

## Related

`docs/adr/0005-mesh-monitor-dashboard.md` (the dashboard this protects),
`PRODUCTION-HARDENING.md` (TLS, still deferred),
`src/SyncMesh.MeshMonitor.Api/Auth/` (implementation),
`tests/SyncMesh.MeshMonitor.Tests` (`TicketStoreTests`, `TicketHasherTests`)
