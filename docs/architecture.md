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
| `OpenFno.Broker.Domain` | Instruments, the order state machine, the broker profile, order rules, margin, matching, charges, position maths, the rate limiter, TOTP, the event types. No I/O. | nothing |
| `OpenFno.Broker.Application` | The engine, its state, matching, risk, settlement, the ports it needs (journal, clock, quotes, instruments, scheduler, secret protector), the event hub and the request log. | Domain |
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
   place ─▶ TRANSIT ──(exchange ack)──▶ OPEN ◀── modify ──▶ TRIGGER_PENDING
      │        │                        │  │                      │
      │        │ (exchange reject,      │  │ part fills           │ trigger
      ▼        │  IOC with nothing      │  ▼                      ▼
  REJECTED     │  to match)             │ PARTIALLY_FILLED ──▶ FILLED
               │                        │  │
               ▼                        ▼  ▼
           CANCELLED              CANCELLED / EXPIRED  (the unfilled part)
```

`OrderLifecycle` is the table of allowed moves. `OrderState.MoveTo` checks
every move against it, so an impossible history (a fill after a cancel) throws
instead of being recorded.

A fill moves the order to `PARTIALLY_FILLED` and then to `FILLED`; what is
left of a partly filled order can still be cancelled, or expires with the
session. An order the exchange refuses after accepting it (the sandbox can
make that happen) ends as `REJECTED` with `EXCHANGE_REJECTED`.

An accepted order is `TRANSIT` until the simulated exchange acknowledges it.
The acknowledgement comes after a configurable latency: `Exchange:AckLatencyMs`
plus up to `Exchange:AckJitterMs` of jitter. An order in transit cannot be
modified or cancelled yet (`ORDER_IN_TRANSIT`), which a real engine has to
handle. The order history records both timestamps, and the back office reports
the acknowledgement time as p50/p95.

## Matching

The broker does not see a real order book, only a quote: best bid, best ask,
their sizes and the last trade. `MatchingService` listens to the quote book
and matches an order the moment its symbol's quote changes; a symbol with no
working order costs one dictionary lookup, and a burst of ticks for one symbol
collapses into a single match.

`Matcher.Match` leans pessimistic on purpose, so a strategy never gets a fill
here that it would not get in the market:

| Situation | What happens |
|---|---|
| An arriving order crosses the spread | Fills at the *opposite* side's price, for at most the quantity shown there |
| A resting order is crossed | Fills at its own limit price |
| A trade prints exactly at a resting order's price | Nothing. Its place in the queue is unknown. `Exchange:FillOnTouch` turns this into a fill |
| The market trades through a resting order | Fills at its limit price |
| A stop's trigger is reached by the last trade | It becomes a live order, then matches under the rules above |
| An IOC order does not fill at once | The rest is cancelled |
| The quote is older than `Exchange:MaxQuoteAgeSeconds` (120 s), or the feed is paused, or the market is closed | Nothing matches |

A fill is one event. It carries everything computed at that moment — price,
quantity, charges, the margin left blocked, realised profit and the position
after it — so a replay of the journal never re-prices anything.

## Money

Each account holds a ledger balance, today's realised profit and today's
charges, and the engine works out the rest:

```
cash      = ledger balance + realised today − charges today
available = cash − margin blocked by working orders − margin held by positions
            + min(0, unrealised P&L)
```

An unrealised profit cannot be spent; an unrealised loss is taken away at
once, as a broker's RMS does. The margin of an order that *closes* an existing
position is zero, after allowing for other working orders already closing it,
so an exit is never refused for want of funds.

Positions net by symbol and product, with an average price for each side, and
carry the day's realised profit and charges. A delivery buy becomes a holding
at the day's close.

## End of day

`MarketClockService` sweeps every few seconds (`Broker:ClockSweepSeconds`) and
does three jobs: expire day orders whose session has closed, square off
intraday positions past their exchange's square-off time, and, once past
`Exchange:SettlementTime` (23:58 IST), settle the trading day. What settlement
does, and how it differs from a real exchange's, is in
[rules.md](rules.md#9-end-of-day).

## Live events

`EventHub` broadcasts every journaled event as it is applied. Two WebSocket
endpoints read it: `/api/v1/stream?access_token=…` for one account, and
`/admin/stream?key=…` for every account. The first message is
`{"event":"stream.connected"}`; then each event arrives as the journal stores
it, with secrets redacted. This is how a client learns that an order was
acknowledged, triggered, filled or rejected without polling.

## The sandbox

Three switches make failures reproducible, all off by default and all changed
from the back office at runtime:

- **A market that moves.** `MarketSimulator` walks a random price per symbol
  and publishes a bid, an ask and depth into the quote book, as if the feed
  had sent them. Orders then trigger and fill at a weekend or at night. It
  must not run on symbols the live feed also prices.
- **Chaos.** `ChaosSettings` adds latency to exchange acknowledgements, makes
  the exchange reject a share of orders after accepting them, answers a share
  of successful calls with `504` (the broker did the work, the client never
  heard), refuses a share with `503`, and pauses the feed. A client that
  survives these survives a real broker's bad afternoon.
- **End the day now.** `POST /admin/end-of-day` runs settlement for any date,
  so a whole trading day takes a minute to rehearse.

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

## The web console

`web/` is a React and TypeScript single-page app, built into the API's
`wwwroot` and served by the broker itself. Any path that is not an API route
gets the console's index page. An unknown API route still gets a JSON `404`.

| Page | Shows | Reads |
|---|---|---|
| Overview | Market status, the feed, today's orders and rejections, p50/p95 of order calls and exchange acknowledgements, live client calls | `/admin/overview`, `/admin/requests` |
| Accounts | Accounts; opening one shows its TOTP secret as a QR code, once | `/admin/accounts` |
| Account | Order book with each order's timeline, positions, trades, holdings, the day's contract note, funds and ledger, API apps, journal, request latencies, the kill switch | `/admin/accounts/{id}/…` |
| Activity | Every call, refusals and failed logins included | `/admin/requests` |
| Trader terminal | Log in as a client, place, modify and cancel orders, watch positions and trades update live, exit a position, pull the kill switch | the public `/api/v1` API and `/api/v1/stream`, with a bearer token |
| Sandbox | Run the offline market, inject chaos, end the trading day | `/admin/simulator`, `/admin/chaos`, `/admin/end-of-day` |
| Rules & calendar | The profile, instrument lookup, a hand-set quote, the holiday calendar with circulars | `/admin/profiles`, `/admin/calendar`, `/admin/instruments` |

The back office keeps the admin key in the tab's `sessionStorage`. The
terminal takes no shortcuts: it logs in with an app and a TOTP code, and its
orders pass the static-IP check and rate limits like any client's.

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

- SPAN plus exposure margin from the exchanges' risk-parameter files. The
  margin model is a flat percentage per segment (see
  [rules.md](rules.md#margin-model)).
- A real order book with queue position. Matching works off one quote per
  symbol, which is why it refuses to fill on a touch.
- Physical settlement of stock derivatives, and T+1 delivery.
- Snapshots, so that replay stays fast as the journal grows.
- Order slicing above the freeze quantity, cover and bracket orders, GTT.
