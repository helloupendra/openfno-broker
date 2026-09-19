# 5. Invalid requests are errors; risk failures are rejected orders

**Status:** accepted, September 2026

## Context

A client needs to know whether an order exists. Indian brokers draw the line
in the same place: input errors (lot size, tick size, missing price) are
refused with no order created, and RMS failures (margin, holdings) create an
order in `REJECTED` status with a reason.

## Decision

`OrderRules.CheckRequest` failures return `400` with a code and create
nothing. `OrderRules.CheckRisk` failures are journaled as `order.rejected` and
returned as an order. Refused modifications and cancellations of an existing
order are journaled as `order.amend_rejected`, so they show in its history.

## Consequences

- The engine must handle both shapes of failure, as it will with a real broker.
- The order book shows RMS rejections, which is where a trader looks for them.
- Input errors do not reach the journal. They are in the request log with
  their code, including the body the client sent.
