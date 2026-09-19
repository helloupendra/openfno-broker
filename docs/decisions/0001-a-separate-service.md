# 1. The broker is a separate service with its own database

**Status:** accepted, September 2026

## Context

The OpenFNO platform's paper trading fills an order at the current price
inside the same process that decided to trade. That hides everything a real
broker does between the decision and the fill: authentication, rate limits,
static-IP checks, risk rejections, exchange latency, orders caught in transit.
An engine that has never met those will meet them for the first time with
real money.

## Decision

The simulated broker is its own service, in its own repository, with its own
database. It is reached over HTTP like a real broker. The engine talks to it
through the same kind of adapter it will use for FYERS.

## Consequences

- The engine's broker code path is exercised end to end before going live.
  Swapping the simulator for a real broker changes the adapter, not the engine.
- Everything that crosses the wire can be observed from the broker's side:
  what arrived, what was refused and why, and how long each step took.
- The broker can run for other people, as a sandbox, without the platform.
- There is one more service to deploy and keep running.
