# 2. State is an event journal replayed into memory

**Status:** accepted, September 2026

## Context

A broker needs a complete, ordered history: which order was placed, from
which IP, what the RMS said, when the exchange acknowledged it. It also needs
current state that is fast to read: the order book and available funds.

## Decision

The journal of events is the only durable state. One writer, `BrokerEngine`,
decides each command's events, appends them, and then applies them to memory
through a single `Apply` method. At start-up, the journal is replayed through
the same method. Events carry every computed value (margin, expiry, reasons),
so replay never recomputes against current prices.

## Consequences

- The audit trail and the state cannot disagree: the state *is* the replayed
  audit trail.
- Restart safety is testable. Run a scenario, replay into a new engine, and
  compare.
- Replay time grows with the journal. Snapshots will be needed once replay
  takes seconds; at simulator volumes that is far off.
- Queries over long history (last year's order book) are served from the
  journal table, not from memory.
