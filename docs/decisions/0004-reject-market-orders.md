# 4. Market orders are rejected, not converted

**Status:** accepted, September 2026

## Context

The exchanges do not permit market orders from algos (NSE NNF 8.1.12), and
fine the broker ₹1,000 for each one. Brokers handle this in different ways.
FYERS converts a market order to a Market Price Protection limit order.
Zerodha requires a market-protection value and rejects protection 0.

## Decision

The simulator rejects `MARKET` and `STOP_MARKET` orders with
`MARKET_ORDER_NOT_ALLOWED`. The FYERS profile says so openly, as a deviation.

## Consequences

- The engine is pushed to price every order itself, the only behaviour that
  is safe at every broker.
- The simulator does not reproduce FYERS's MPP conversion. We have not found
  the protection band's width in a primary FYERS source; guessing it would
  make fills look more certain than they are. If the band is published, a
  profile flag can switch to conversion.
