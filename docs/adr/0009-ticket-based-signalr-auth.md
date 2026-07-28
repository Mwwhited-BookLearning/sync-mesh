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
- **Confirmed via a real-browser check (not just unit tests): a secure
  context is a hard functional requirement, not just a security
  nice-to-have.** `computeHashedTicket` uses `crypto.subtle`
  (`SubtleCrypto.importKey`/`sign`), which browsers only expose in a
  secure context — HTTPS, or the literal hostname `localhost` (an
  explicit browser-security carve-out for local development, not a
  general "any local network" exception). Verified directly: the same
  frontend served from `http://host.docker.internal:5299` (a real
  backend, not mocked) failed the SignalR handshake with `Cannot read
  properties of undefined (reading 'importKey')` — `crypto.subtle` was
  simply `undefined` — while the exact same code serving from
  `http://localhost:...` works (this is what
  `tests/e2e/smoke.spec.ts` actually exercises, via Vite's dev server on
  `localhost`). Concretely: this dashboard **will not connect at all**
  once accessed via any hostname other than `localhost` unless it's
  served over HTTPS — not degraded, not slower, `POST /auth/ticket`
  succeeds but the client-side hash computation throws. This raises the
  stakes on `PRODUCTION-HARDENING.md`'s "no TLS yet" item for this
  dashboard specifically: it's no longer just a confidentiality gap, it's
  the difference between the dashboard working or not for any operator
  reaching it by IP address or a real hostname.

## Amendment (2026-07-28) — frontend wired up, design doc added

The frontend side (`web/mesh-monitor`) now actually implements this flow
— previously only the backend existed, verified via raw HTTP calls; the
dashboard itself had no way to authenticate at all. New: `ConnectView`
(token entry, gates the rest of the app), `stores/authStore.ts` (holds
the bearer token in memory only — no localStorage, see its own doc
comment), `services/auth.ts` (`mintTicket`/`computeHashedTicket` via Web
Crypto, cross-checked against Node's `crypto` module in
`tests/unit/auth.spec.ts` to confirm it matches
`TicketHasher.Compute` byte-for-byte). `signalrClient.ts`'s hub
connection now uses `accessTokenFactory` — @microsoft/signalr's own
extensibility point for "mint a fresh credential before every (re)connect
attempt," which is exactly what a single-use ticket needs (a reconnect
can't resend the one already redeemed). This is also why the query
parameter this handler accepts is `access_token`, not just `ticket` —
that's the name SignalR's client sends, not configurable client-side; the
handler accepts both.

Full sequence diagram and component diagram (backend + frontend
together):
`docs/bdd/design/mesh-monitor-ticket-auth.md`. Wireframe for the new
Connect screen: `docs/ui-wireframes.md` → "Layer 0."

## Amendment (2026-07-28) — negotiate 401 fixed: handler must accept `Bearer <hash>` too

The frontend wiring above shipped with a bug that broke every connection
attempt: `@microsoft/signalr`'s `AccessTokenHttpClient` sends
`Authorization: Bearer <accessTokenFactory-value>` on **every** HTTP
request it issues through the connection — including `POST /negotiate`,
which happens before any WebSocket exists and therefore before the
`?access_token=` query-string path (used only by `WebSocketTransport`
for the actual WS upgrade) is even reachable. This is hardcoded in the
client (`AccessTokenHttpClient._setAuthorizationHeader`, not
configurable), not something this project's client-side code chose.
`TicketAuthenticationHandler` only recognized an `Authorization: Ticket
<hash>` header, so `/negotiate` 401'd on the very first request of every
connection attempt — the ticket exchange itself worked, but the
connection it was meant to protect never actually completed.

Fixed by accepting `Bearer <hash>` as an equally valid header form for
ticket redemption, alongside the existing `Ticket <hash>` header and the
`access_token`/`ticket` query parameters. This doesn't create ambiguity
with real JWT bearer tokens: `ITicketStore.TryRedeem` simply fails for
any value that isn't a currently-outstanding ticket hash, so a real JWT
sent as `Bearer <jwt>` falls through this scheme's `NoResult`/`Fail` and
is still evaluated by the `JwtBearer` scheme in the same
`AddAuthenticationSchemes(AuthSchemeNames.BearerOrTicket)` policy.

## Related

`docs/adr/0005-mesh-monitor-dashboard.md` (the dashboard this protects),
`docs/bdd/design/mesh-monitor-ticket-auth.md` (sequence + component
diagrams, both backend and frontend),
`docs/ui-wireframes.md` (Layer 0 — Connect Screen),
`PRODUCTION-HARDENING.md` (TLS, still deferred),
`src/SyncMesh.MeshMonitor.Api/Auth/` (backend implementation),
`web/mesh-monitor/src/{stores/authStore.ts,services/auth.ts,views/ConnectView.*}`
(frontend implementation),
`tests/SyncMesh.MeshMonitor.Tests` (`TicketStoreTests`, `TicketHasherTests`),
`web/mesh-monitor/tests/unit/{auth,authStore}.spec.ts`
