# Architecture

OpenFNO Broker is a stand-alone service. It behaves like an Indian stock
broker's trading API, so a trading engine can be built and tested against
realistic rules before it touches real money. It is not a backtester. It holds
accounts and orders the way a broker does: over time, across restarts, with a
record of every step.

## The pieces

```
                    ┌────────────────────────────── OpenFno.Broker.Api ──────────────────────────────┐
 trading engine ──▶ │ RequestTrace ─▶ SessionFilter ─▶ StaticIpFilter ─▶ RateLimitFilter ─▶ endpoint │
 (HTTP + token)     │      │                                                                  │       │
                    │      └──▶ RequestLog ──▶ Postgres request_log                           ▼       │
 back office  ────▶ │ AdminKeyFilter ─▶ admin / back-office endpoints ───────────────▶ BrokerEngine   │
 (web console)      └──────────────────────────────────────────────────────────────────────┬─────────┘
                                                                                          │
         ┌───────────────────────── OpenFno.Broker.Application ──────────────────────────┐ │
         │ BrokerEngine: one lock ─▶ decide (Domain rules) ─▶ journal ─▶ apply to state   │◀┘
         │ BrokerState: accounts, apps, sessions, orders — rebuilt from the journal       │
         └───────┬──────────────────────┬───────────────────────┬────────────────────────┘
                 │ IJournal              │ IQuoteBook            │ IInstrumentCatalog
          Postgres journal        Redis tick feed         FYERS + Dhan masters
          (or in memory)          (market:ticks)          (data/instruments)
```

| Project | Holds | Depends on |
|---|---|---|
| `OpenFno.Broker.Domain` | Instruments, the order state machine, the broker profile, order rules, margin, the rate limiter, TOTP, the event types. No I/O. | nothing |
| `OpenFno.Broker.Application` | The engine, its state, the ports it needs (journal, clock, quotes, instruments, scheduler, secret protector), and the request log. | Domain |
| `OpenFno.Broker.Infrastructure` | The Postgres journal and request-log writer, the Redis tick feed, the instrument-master and calendar loaders, data protection. | Application |
| `OpenFno.Broker.Api` | HTTP: endpoints, filters, request tracing, hosting. | Infrastructure |

## One writer, one journal

All state changes go through `BrokerEngine`, one command at a time:

1. **Decide.** Against the current state, work out which events happen, or why
   none can. The rules are pure functions in the domain.
2. **Journal.** Append those events to the journal. Only then does anything change.
3. **Apply.** Apply the events to the in-memory state, then answer.

`BrokerState.Apply` is the only method that changes state. It runs for live
events and replayed events alike, so a restart replays the journal and ends in
exactly the state it left. Events carry every value that was computed when
they happened, such as blocked margin, session expiry and rejection reason. A
replay never re-runs a rule against today's prices.

This gives a few things for free:

- **Audit trail.** The journal is the record a broker must keep. It shows who
  did what, from which IP, when, and what the broker answered.
- **Exact ordering.** Sequence numbers are gap-free. A Postgres primary key on
  `seq` means a second broker process on the same database fails at its first
  write, instead of interleaving histories.
- **Tests without mocks of the state.** Replay tests run a scenario, start a
  second engine on the same journal, and compare.

One lock is enough for what this service serves: a few clients, at most tens
of orders per second (the exchanges cap an unregistered algo at 10). If that
ever changes, the state splits cleanly by account.

Authentication is on the hot path of every request, so it does not take the
lock. Sessions, apps and accounts sit in concurrent maps, and the values read
from them are replaced whole, never mutated in place.

## Two kinds of refusal

This follows what Indian brokers do:

- **An invalid request** (wrong lot multiple, off-tick price, a market order,
  market closed) is answered with `400` and a code. No order exists. It is in
  the request log and not in the journal.
- **A valid order that fails a risk check** (insufficient margin, no holdings,
  price band, intraday cut-off) becomes an order with status `REJECTED`. It
  appears in the order book with its reason, as a broker's RMS rejection does.

A modify or cancel that is refused on an existing order is journaled as
`order.amend_rejected`. It shows in that order's history.

## Order lifecycle

```
            place ─▶ TRANSIT ──(exchange ack)──▶ OPEN ◀──modify──▶ TRIGGER_PENDING
               │         │                        │ │                    │
               ▼         │                 cancel │ │ session close      │
           REJECTED      │ (IOC, nothing          ▼ ▼                    ▼
                         │  to match)       CANCELLED  EXPIRED    (same exits)
                         └──────────────▶ CANCELLED
```

`OrderLifecycle` is the table of allowed moves. `OrderState.MoveTo` checks
every move against it, so an impossible history (a fill after a cancel) throws
instead of being recorded.

An accepted order is `TRANSIT` until the simulated exchange acknowledges it.
The acknowledgement comes after a configurable latency: `Exchange:AckLatencyMs`
plus up to `Exchange:AckJitterMs` of jitter. An order in transit cannot be
modified or cancelled yet (`ORDER_IN_TRANSIT`), which a real engine has to
handle. The order history records both timestamps, and the back office reports
the acknowledgement time as p50/p95.

## Latency tracing

Every API call gets a `RequestTrace`, which records where its time went:

| Field | Time spent in |
|---|---|
| `authMs` | Resolving the access token |
| `rateLimitMs` | The rate limiter |
| `queueMs` | Waiting for the engine's lock |
| `decideMs` | Rules and margin |
| `journalMs` | Writing the journal |
| `totalMs` | All of it, first byte in to response out |

Each finished trace goes to `RequestLog`. It keeps the last 500 calls per
client and the last 2,000 overall in memory for the API, and writes every
entry to the Postgres `request_log` table in batches, off the request path.

## Market data and instruments

- **Instruments** come from FYERS's public symbol masters: symbols, lot sizes,
  tick sizes and sessions. Freeze quantities come from Dhan's detailed scrip
  master, joined on exchange and exchange token, and commodity lot sizes from
  `data/reference/commodity-lot-sizes.json`. `scripts/fetch-instruments.sh`
  downloads the masters.
- **Prices** come from the OpenFNO platform's Redis stream `market:ticks`, read
  from the moment the broker starts. With no feed configured, quotes can be set
  by hand through `PUT /admin/quotes`.
- **Trading days** come from `data/calendar/*.json`, one file per year, built
  from the exchanges' holiday circulars. It covers full closures, MCX
  half-day closures and special sessions such as a Sunday budget session.

## Security

| Concern | How it is handled |
|---|---|
| App secrets and access tokens | 256-bit random. Only their SHA-256 is stored. They are shown once. |
| TOTP secrets | Encrypted with ASP.NET Core data protection before they reach the journal. The key ring lives in `data/keys` and **must be backed up with the database**: without it no account can log in. |
| Journal output | `GET /journal` hides secret fields and their hashes. |
| Caller IP behind a proxy | `Broker:ClientIpHeader` (for example `CF-Connecting-IP`) is believed only when the connection comes from this machine, never from the internet. |
| Admin API | Off unless `Broker:AdminKey` is set; the key is compared in constant time. |
| Login guessing | Login is rate-limited per caller IP. A TOTP code is valid once. |

## Not built yet

- A matching engine. Orders rest; nothing fills yet. Next: fills from live
  ticks and depth, partial fills, tradebook, positions, holdings, P&L, charges
  and contract notes.
- Intraday auto square-off, a client kill switch, end-of-day settlement.
- SPAN margins from exchange files.
- Snapshots, so that replay stays fast as the journal grows.
