# API

The machine-readable version is served at `/openapi/v1.json`. This page covers
the conventions and a full session from start to finish.

## Conventions

- JSON. Property names are camelCase. Enums are `UPPER_SNAKE` strings, never
  numbers.
- Money is in rupees, quantities are in units (lots × lot size), and times are
  ISO 8601 with an offset.
- A missing required field, a null where a value is required, or an unknown
  enum value is refused with `400 INVALID_REQUEST`.
- Every error has one shape:

```json
{ "error": { "code": "LOT_SIZE_MULTIPLE", "message": "Quantity 70 is not a multiple of the lot size 65." } }
```

| HTTP | Meaning |
|---|---|
| 200 | Done. For `POST /orders` this includes orders with status `REJECTED`: the order exists, and the broker's risk checks refused it |
| 400 | The request is not valid. Nothing was created |
| 401 | No session, an expired session, or wrong login details |
| 403 | Not allowed from this IP, or the admin API is off |
| 404 | No such order, account or instrument |
| 409 | The order is in a state that does not allow this (in transit, already final) |
| 429 | Rate limit. Wait `Retry-After` seconds |
| 503 | The broker is starting |

## A session from start to finish

The examples use a local broker on port 5310 in Development mode. In that
mode the admin key is `dev-admin-key`.

**1. The back office opens an account, pays in, and issues an API app.**

```sh
curl -s -X POST localhost:5310/admin/accounts -H 'X-Admin-Key: dev-admin-key' \
  -H 'Content-Type: application/json' -d '{"name":"Asha"}'
# {"clientId":"OFB00001","totpSecret":"JBSW...","totpUri":"otpauth://totp/..."}

curl -s -X POST localhost:5310/admin/accounts/OFB00001/funds -H 'X-Admin-Key: dev-admin-key' \
  -H 'Content-Type: application/json' -d '{"amount":200000,"reference":"UPI 1234"}'

curl -s -X POST localhost:5310/admin/accounts/OFB00001/apps -H 'X-Admin-Key: dev-admin-key' \
  -H 'Content-Type: application/json' -d '{"staticIps":["127.0.0.1"]}'
# {"clientId":"OFB00001","appId":"APP-7K2M9Q4DXA","appSecret":"...","staticIps":["127.0.0.1"]}
```

Add the TOTP secret to any authenticator app. The `totpUri` works as a QR
code.

**2. The client logs in for the day.**

```sh
curl -s -X POST localhost:5310/api/v1/session -H 'Content-Type: application/json' \
  -d '{"appId":"APP-7K2M9Q4DXA","appSecret":"...","clientId":"OFB00001","totp":"123456"}'
# {"accessToken":"...","clientId":"OFB00001","appId":"APP-7K2M9Q4DXA","expiresAt":"2026-09-19T06:00:00+05:30"}
```

**3. Place, modify, cancel.** Send the token as `Authorization: Bearer <token>`.

```sh
curl -s -X POST localhost:5310/api/v1/orders -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"symbol":"NSE:NIFTY26SEPFUT","side":"BUY","quantity":65,"type":"LIMIT","product":"NRML","limitPrice":25000,"tag":"breakout-1"}'
# {"orderId":"26091800000001","status":"TRANSIT","blockedMargin":195000.00,"algoId":"99999",...}

curl -s -X PATCH localhost:5310/api/v1/orders/26091800000001 -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"limitPrice":24990}'

curl -s -X DELETE localhost:5310/api/v1/orders/26091800000001 -H "Authorization: Bearer $TOKEN"
```

| Field | Values |
|---|---|
| `side` | `BUY`, `SELL` |
| `type` | `LIMIT`, `STOP_LIMIT` (SL). `MARKET` and `STOP_MARKET` (SL-M) are refused for API orders |
| `product` | `CNC` (delivery, cash only), `MIS` (intraday), `NRML` (carry-forward derivatives) |
| `validity` | `DAY` (default), `IOC` (not in commodities) |
| `tag` | Optional, 1–20 letters, digits, `-` or `_` |

**4. See what happened.**

| Call | Shows |
|---|---|
| `GET /api/v1/orders?date=2026-09-18` | The order book for an IST trading day |
| `GET /api/v1/orders/{id}` | One order and its history: every step with its time |
| `GET /api/v1/funds` | Pay-ins, blocked margin, available funds, ledger |
| `GET /api/v1/journal?after=0` | Every event recorded for the account |
| `GET /api/v1/requests` | The account's latest calls with status, refusal code and milliseconds per step |
| `GET /api/v1/profile` | The limits the account trades under |
| `GET /api/v1/instruments?symbol=NSE:NIFTY26SEPFUT` | Lot size, tick size, freeze quantity, session |
| `GET /api/v1/quotes?symbol=NSE:NIFTY26SEPFUT` | The last price the simulated exchange has |
| `GET /api/v1/whoami` | The IP address the broker sees for you, which is the one the static-IP check compares. No token needed |

**5. What the orders became.**

| Call | Shows |
|---|---|
| `GET /api/v1/trades?date=2026-09-18` | Every fill of a trading day: price, quantity, charges, and the realised profit it booked |
| `GET /api/v1/positions` | Open positions and the ones closed since the last settlement: quantity, average price, last price, unrealised and realised P&L, charges today, margin |
| `GET /api/v1/holdings` | Shares held from earlier days, with what they cost and what they are worth now |
| `GET /api/v1/contract-notes?date=2026-09-18` | The day's trades with buy and sell turnover, every charge line, and the net after charges |
| `GET /api/v1/kill-switch` | Whether it is on, since when, until when and who set it |
| `POST /api/v1/kill-switch` | `{"active":true,"squareOff":true}` stops the account: every working order is cancelled and, with `squareOff`, every position is closed. A client cannot turn it off again before 06:00 IST the next day (`409 KILL_SWITCH_ACTIVE`); the back office can |

One lot of NIFTY futures bought at 25,000.50, with the market at 25,000:

```sh
curl -s localhost:5310/api/v1/positions -H "Authorization: Bearer $TOKEN"
# [{"symbol":"NSE:NIFTY26SEPFUT","product":"NRML","quantity":65,"averagePrice":25000.5,
#   "lastPrice":25000,"unrealised":-32.5,"realisedToday":0,"chargesToday":91.19,
#   "netToday":-123.69,"buyQuantity":65,"buyAverage":25000.5,"margin":195003.9,...}]

curl -s localhost:5310/api/v1/trades -H "Authorization: Bearer $TOKEN"
# [{"tradeId":"260920000000001","orderId":"26092000000001","symbol":"NSE:NIFTY26SEPFUT",
#   "side":"BUY","quantity":65,"price":25000.5,"maker":false,
#   "charges":{"brokerage":20,"transactionTax":0.0,"exchangeFee":28.11,"sebiFee":1.63,
#              "stampDuty":32.5,"gst":8.95,"other":0,"total":91.19},
#   "realised":0,"tradingDate":"2026-09-20","tag":"docs"}]
```

`netToday` is what the position is worth after its charges: realised plus
unrealised, less the charges it has paid today.

**6. Live events, without polling.**

```sh
websocat "ws://localhost:5310/api/v1/stream?access_token=$TOKEN"
# {"event":"stream.connected","clientId":"OFB00001"}
# {"event":"order.placed","orderId":"26091800000001",...}
# {"event":"order.accepted","orderId":"26091800000001",...}
# {"event":"order.filled","orderId":"26092000000001","tradeId":"260920000000001",
#  "price":25000.5,"quantity":65,"maker":false,"charges":{...,"total":91.19},
#  "realised":0,"orderMarginAfter":0,"positionAfter":{"quantity":65,...}}
```

Every event is the one the journal stored, with secrets removed. Browsers
cannot set headers on a WebSocket, so the token goes in the query string.

## Order statuses

| Status | Meaning |
|---|---|
| `TRANSIT` | Passed the broker's checks, on its way to the exchange |
| `OPEN` | Resting in the exchange's book |
| `TRIGGER_PENDING` | A stop order waiting for its trigger |
| `PARTIALLY_FILLED`, `FILLED` | Filled in part, or in full. What is left of a partly filled order can still be cancelled |
| `CANCELLED` | Cancelled by the client, or the unfilled part of an IOC order |
| `REJECTED` | Refused by the broker's risk checks; `rejectionCode` says why |
| `EXPIRED` | A day order still working when its session closed |

## Refusal codes

| Code | Where |
|---|---|
| `INVALID_REQUEST`, `UNKNOWN_SYMBOL`, `INSTRUMENT_NOT_TRADABLE`, `INSTRUMENT_EXPIRED`, `INSTRUMENT_NOT_CONFIGURED`, `MARKET_CLOSED`, `INVALID_QUANTITY`, `LOT_SIZE_MULTIPLE`, `FREEZE_QUANTITY`, `PRODUCT_NOT_ALLOWED`, `MARKET_ORDER_NOT_ALLOWED`, `IOC_NOT_ALLOWED`, `INVALID_PRICE`, `TICK_SIZE_MULTIPLE`, `INVALID_TRIGGER_PRICE`, `INVALID_TAG` | `400`, no order created |
| `INSUFFICIENT_FUNDS`, `NO_HOLDINGS`, `PRICE_BAND`, `INTRADAY_CUTOFF` | An order with status `REJECTED`; also `409` on a refused modify |
| `ORDER_NOT_FOUND`, `ORDER_IN_TRANSIT`, `ORDER_NOT_MODIFIABLE`, `MODIFICATION_LIMIT` | Modify and cancel |
| `UNAUTHENTICATED`, `SESSION_EXPIRED`, `INVALID_CREDENTIALS`, `INVALID_TOTP` | `401` |
| `STATIC_IP_MISMATCH` | `403` on place, modify or cancel from an IP the app has not whitelisted |
| `STATIC_IP_CHANGE_LIMIT` | `409`, the weekly change is used |
| `RATE_LIMITED`, `DAY_BLOCKED` | `429` |
| `KILL_SWITCH_ACTIVE` | The account is stopped: a new order is recorded as `REJECTED`, a modify is refused with `409`, and a client's attempt to lift the switch early gets `409` |
| `EXCHANGE_REJECTED` | The exchange refused the order after accepting it (the sandbox can make this happen); the order ends `REJECTED` |
| `CHAOS_UNAVAILABLE`, `CHAOS_LOST_RESPONSE` | `503` and `504` from the sandbox's fault injection |
| `NOT_FOUND`, `INTERNAL_ERROR` | `404` and `500` |

The value behind each rule, with its source, is in [rules.md](rules.md).

## Back office

`/admin/*` needs the `X-Admin-Key` header. It opens accounts, moves funds,
issues apps, sets quotes by hand when there is no feed, and reads any
account's orders, trades, positions, holdings, contract notes, funds, journal
and requests. `/admin/overview` reports market status, the feed, today's
totals, and the p50/p95 latency of order placement and of exchange
acknowledgement. `/admin/stream?key=…` is the same live event stream, for
every account at once.

## Sandbox

These make an offline market and a bad afternoon reproducible. All of it is
off until it is switched on.

| Call | Does |
|---|---|
| `PUT /admin/simulator` | `{"running":true,"symbols":[{"symbol":"NSE:NIFTY26SEPFUT"}],"volatilityPercent":0.05,"intervalMs":1000,"spreadTicks":2,"depthLots":20}` walks a price per symbol with a bid, an ask and depth, so orders trigger and fill at night or at a weekend. Do not run it on symbols the live feed also prices |
| `GET /admin/simulator` | Whether it runs, and its symbols with their last price |
| `PUT /admin/chaos` | `{"extraAckLatencyMs":0,"exchangeRejectPercent":0,"lostResponsePercent":0,"unavailablePercent":0,"feedPaused":false}` — slow acknowledgements, exchange rejections after acceptance, `504` answers to calls that did work, `503` refusals, and a stopped feed |
| `GET /admin/chaos` | The current settings |
| `POST /admin/end-of-day` | `{"tradingDate":"2026-09-18"}` (or nothing for today) settles the day at once: day orders expire, intraday positions close, expired contracts settle, futures mark to market, delivery moves into holdings, and the ledger is posted |
| `PUT /admin/quotes` | Sets one symbol's bid, ask and last price by hand |

A fill needs a quote that is newer than `Exchange:MaxQuoteAgeSeconds`
(120 seconds by default). With no live feed, either set a quote by hand or
start the simulator; a stale quote never fills anything.
