# 3. Broker rules are typed profiles in code, with sources in the docs

**Status:** accepted, September 2026

## Context

Brokers differ in their limits: requests per second, IPs per app, whether a
market order is converted or rejected, how many modifications an order may
take. The values change when SEBI or an exchange issues a circular.

## Decision

Each broker is a `BrokerProfile` record in code. FYERS is the first. Each
account trades under one profile. Every value's source, or the fact that it is
an assumption, is written down in `docs/rules.md` next to the code that
enforces it.

## Consequences

- A rule change is a reviewed diff with a test, not a configuration edit that
  nobody sees.
- The simulator can be pointed at another broker's behaviour by adding a
  profile.
- Changing a profile needs a release. For rules that change a few times a
  year, that is acceptable.
