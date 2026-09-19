# OpenFNO Broker

A simulated Indian stock broker. Trading engines connect to it to meet the
rules they will meet at a real broker, before real money is at stake.

It enforces the SEBI retail-algo framework, in force for every broker since
1 April 2026: static-IP whitelisting, a daily two-factor login, 10 order
operations per second, no market orders, and an algo tag on every order. It
also enforces each exchange's lot sizes, tick sizes, freeze quantities,
trading hours and holidays, and a broker's risk checks: margin, holdings,
price bands and the intraday cut-off. Every order, refusal and login is
recorded in an append-only journal. Every API call is logged with the
milliseconds it spent at each step.

It is part of [OpenFNO](https://openfno.com), an open-source algo-trading desk
for Indian F&O. The trading engine there will place its orders here first.

> **Not a real broker.** No money moves and nothing reaches an exchange. The
> rules are taken from public circulars and broker documentation (see
> [docs/rules.md](docs/rules.md)); where a value is our assumption, the docs
> say so. Not affiliated with SEBI, NSE, BSE, MCX, FYERS or Dhan.

## Why

Paper trading usually fills an order the moment the strategy decides to
trade. A real broker first checks the session, the caller's IP, the rate, the
lot, the tick, the margin and the market hours. Then the exchange takes a few
milliseconds to acknowledge the order, and during that time the order cannot
be changed. An engine that has only met the first world meets the second with
real money. This service is the second world, without the money.

## What it does today

| | |
|---|---|
| **Accounts** | Client IDs, pay-ins and pay-outs with a ledger, API apps with whitelisted static IPs (one change per calendar week) |
| **Login** | App credentials plus a TOTP code, once a day; sessions end at 06:00 IST; a code works once |
| **Orders** | Place, modify and cancel for LIMIT and STOP_LIMIT orders on NSE, BSE and MCX cash, F&O and commodities; DAY and IOC validity; tags; the exchange's acknowledgement after a configurable latency; day orders expire at the session close |
| **Checks** | Instrument, market hours and holidays, whole lots, freeze quantity, tick size, stop-trigger geometry, product by segment, market-order and commodity-IOC bans, margin, holdings, price band, intraday cut-off, rate limits |
| **Records** | The journal (every event, gap-free, replayed on start), order histories with a timestamp per step, and a request log with auth, rate-limit, queue, decision and journal time per call |
| **Market** | 1,27,000+ instruments from the FYERS and Dhan public masters; live prices from the OpenFNO platform's Redis tick stream |

Next: a matching engine that fills against live ticks and depth, with a
tradebook, positions, holdings, charges, contract notes, MIS square-off and a
kill switch. See [the roadmap](#roadmap).

## Run it

You need the .NET 10 SDK.

```sh
scripts/fetch-instruments.sh          # ~60 MB of public instrument masters into data/instruments
cd src/OpenFno.Broker.Api
Exchange__AlwaysOpen=true dotnet run  # http://localhost:5310, in-memory, admin key "dev-admin-key"
```

`Exchange__AlwaysOpen=true` ignores trading hours, so you can try it at night
or at a weekend. Leave it off to get real market hours.

With Postgres, so that state survives restarts:

```sh
BROKER_ADMIN_KEY=choose-a-long-random-key docker compose up --build
```

Then follow [docs/api.md](docs/api.md) for a full session from start to
finish: open an account, log in with TOTP, place, modify and cancel orders,
and read the journal.

## How it is built

A single writer applies commands one at a time. Each command's events are
written to the journal before the in-memory state changes. A restart replays
the journal into the same state. The design is in
[docs/architecture.md](docs/architecture.md), and the reasons for its main
choices are in [docs/decisions](docs/decisions).

```
src/
  OpenFno.Broker.Domain          rules, order state machine, margin, rate limiter, TOTP, events — no I/O
  OpenFno.Broker.Application     the engine, its state and ports
  OpenFno.Broker.Infrastructure  Postgres journal, Redis feed, instrument masters, calendar
  OpenFno.Broker.Api             HTTP, filters, request tracing
tests/                           unit, engine, replay and HTTP tests
data/calendar                    exchange holidays and special sessions, with their circulars
data/reference                   commodity lot sizes
```

## Tests

```sh
dotnet test
```

The Postgres tests run when `BROKER_TEST_POSTGRES` points at a throwaway
database. CI starts one.

## Roadmap

1. **Matching.** Fills from live ticks and depth, partial fills, a tradebook,
   positions, holdings, realised and unrealised P&L.
2. **After the fill.** Charges and contract notes (brokerage, STT, exchange
   and SEBI fees, stamp duty, GST), MIS auto square-off, a client kill switch,
   end-of-day settlement.
3. **Web console.** The back office in a browser.
4. **The OpenFNO engine as a client.** A broker adapter in the platform, with
   an execution layer that works out an order's fate from the order book
   after a timeout, as it must with a real broker.

## License

[PolyForm Noncommercial 1.0.0](LICENSE). Free to use, study and change for
any noncommercial purpose.
