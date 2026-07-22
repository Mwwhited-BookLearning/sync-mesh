# ADR-0003: Hybrid Logical Clocks for Cross-Site Event Ordering

| | |
|---|---|
| Status | Accepted |
| Date | 2026-07-22 |
| Deciders | Architecture |

## Context

Events originate at many independent sites (daemons and servers) that may
be disconnected from each other for extended periods. The mesh needs a
deterministic way to order events across sites on replay. Relying on the
transport (NATS) to provide global ordering across gateway/leaf connections
is fragile: true global total order across independently-operating,
occasionally-partitioned sites is expensive or impossible to guarantee, and
coupling correctness to transport behavior makes the system brittle to
transport changes or bugs.

## Decision

Every event carries an explicit Hybrid Logical Clock (HLC) value and
origin site ID, assigned at the originating site. Authoritative ordering
is reconstructed at replay time from these values, never assumed from
transport arrival order. The transport's only job is reliable, fast,
at-least-once delivery — not ordering.

## Considered Alternatives

- **Rely on NATS delivery order** — simplest to implement, but ordering
  guarantees don't hold across gateway/leaf hops spanning independently
  operating sites, especially under partition/reconnect scenarios.
- **Vector clocks** — give precise causal relationships but grow
  proportionally to the number of sites, which becomes unwieldy as the
  mesh scales; HLC gives "close enough to wall-clock, causally consistent"
  ordering with fixed-size metadata.
- **Centralized sequencer (single node assigns global order)** — would
  give true total order, but reintroduces a single point of
  coordination/failure that conflicts with the peer-to-peer, occasionally-
  disconnected nature of the mesh.
- **Wall-clock timestamps only** — simple, but vulnerable to clock skew
  across independently-operated on-prem/WAN/cloud sites; not causally
  consistent.

## Consequences

- Positive: ordering correctness doesn't depend on transport behavior;
  system remains correct even if the transport is swapped later (satisfies
  the earlier "swap broker via config" goal without also having to worry
  about re-deriving ordering guarantees).
- Negative: every consumer must implement idempotent, HLC-aware apply
  logic rather than trusting arrival order — more application-level
  complexity than "just consume in order."
- Follow-up: validate the HLC implementation's clock-skew and counter
  overflow handling with dedicated test scenarios (see
  `docs/bdd/features/event-ordering-and-idempotency.feature`) before
  relying on it in production.

## Related

`docs/06-data-model.md` §3–4, `docs/adr/0002-nats-leaf-nodes-for-transport.md`
