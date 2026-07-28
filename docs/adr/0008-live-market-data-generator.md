# ADR-0008: Live Market-Data Generator as an Alternative Order Source

| | |
|---|---|
| Status | Accepted |
| Date | 2026-07-27 |
| Deciders | Architecture |

## Context

The Order Book demo (`docs/06-data-model.md` §8) so far only generates
orders from `SyntheticOrderGenerator`, whose prices are pure random noise.
A generator driven by real stock prices makes the demo materially more
compelling — you're watching an actual market price flow through the
mesh, not synthetic jitter. This is a genuine architectural first for this
project: **every other component in this repo is self-contained** (NATS,
Postgres/SQLite, named pipes, plain TCP) — this is the first dependency on
a live external network service anywhere in the solution, and needs to be
flagged as such, not folded in as "just another generator."

## Decision

Add `SyncMesh.Daemon.Demo.MarketDataOrderGenerator`, a `BackgroundService`
polling [Twelve Data](https://twelvedata.com)'s `GET /price?symbol=...&apikey=...`
endpoint (a plain REST/JSON call returning `{"price": "..."}`) and placing
an `OrderPlaced` at the real fetched price — same in-process
`LocalEventWriter` write path as `SyntheticOrderGenerator`, same example
domain (`SyncMesh.Contracts.OrderBook`).

- **Zero-setup smart default, verified directly against the live API**:
  Twelve Data's shared `apikey=demo` genuinely returns a live price for
  `AAPL` with no signup — confirmed by hitting the real endpoint. Every
  *other* symbol returns `{"code":401,...}` directing you to request a
  free personal key ("it only takes 10 seconds"). So the default
  configuration is `ApiKey="demo"`, `Symbols=["AAPL"]` — works out of the
  box, matching this project's "smart defaults, zero configuration
  required" convention (`ARCHITECTURE.md` → Configuration) — extending to
  more symbols or a real rate-limit budget requires the user's own free
  key, not a code change.
- **Independent, config-selectable, not a replacement.** Both generators
  run by default (`SyntheticOrderGeneratorOptions.Enabled` and
  `MarketDataOptions.Enabled` are independent flags, both default `true`)
  — a user who wants only real-price-driven orders can disable the
  synthetic one via config, and vice versa, without a code change.
- **Graceful degradation is not optional.** A stock API is a service this
  project doesn't control, can rate-limit, and can be unreachable
  entirely (no network, wrong/expired key, symbol not covered by the
  configured plan). Every one of those must degrade to "skip this tick,
  log a warning, try again next interval" — the exact same pattern
  already established for every other background publisher in this
  codebase (`MonitorPublisher`, `TunnelStatusPublisher`, etc.: "a missed
  tick isn't a correctness problem"). It must never crash-loop the daemon
  process, and it must never block anything else the daemon does — this
  generator has no bearing on the daemon's actual durability/forwarding
  guarantees if it fails entirely.

## Considered Alternatives

- **A bundled historical dataset, replayed offline** — considered first;
  rejected in favor of live data per explicit user preference. Would have
  been fully self-contained (no network dependency at all), at the cost of
  showing recorded rather than real-time prices.
- **Replacing `SyntheticOrderGenerator` outright** — rejected; keeping
  both, independently toggleable, means the zero-setup synthetic path
  keeps working even with no network access at all (e.g., this sandbox
  hit a NuGet-restore network restriction earlier this session — a demo
  that *required* live internet access to produce any mesh traffic at all
  would be a strictly worse default).
- **A different provider** (Alpha Vantage, Finnhub) — Alpha Vantage's free
  tier (25 requests/day) is too restrictive for a generator meant to poll
  continuously during a demo session; Twelve Data's higher free-tier
  budget (order of hundreds of requests/day on a real personal key) and
  its genuinely-free, no-credit-card, instant demo key made it the
  practical choice.

## Consequences

- **Positive**: materially more compelling demo (real prices, not noise);
  zero setup required to see it work at all (one real symbol, `AAPL`);
  clear, low-friction upgrade path (a free personal key) for more symbols
  or headroom.
- **Negative / tradeoffs**: this project now has one component whose
  interesting behavior depends on an external service's continued
  existence and free-tier policy, which could change or disappear —
  acceptable here specifically because failure degrades to "this one demo
  generator produces nothing," never to a crash or a correctness problem
  elsewhere. Also: this is genuinely live data, not deterministic — two
  runs of the demo will show different real prices, unlike a bundled
  dataset would have.
- **Follow-up**: none required; this is demo/example tooling, not a phase
  deliverable (same scope framing as the rest of the Order Book demo, see
  `WORKPLAN.md` → "Ancillary Work").

## Related

`docs/06-data-model.md` §8 (Order Book Example Domain), `ARCHITECTURE.md`
→ "Order Book demo," `src/SyncMesh.Daemon/Demo/SyntheticOrderGenerator.cs`
(the sibling generator this one runs alongside).
