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

## Order statuses

| Status | Meaning |
|---|---|
| `TRANSIT` | Passed the broker's checks, on its way to the exchange |
| `OPEN` | Resting in the exchange's book |
| `TRIGGER_PENDING` | A stop order waiting for its trigger |
| `PARTIALLY_FILLED`, `FILLED` | Reserved for the matching engine |
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

The value behind each rule, with its source, is in [rules.md](rules.md).

## Back office

`/admin/*` needs the `X-Admin-Key` header. It opens accounts, moves funds,
issues apps, sets quotes by hand when there is no feed, and reads any
account's orders, funds, journal and requests. `/admin/overview` reports
market status, the feed, today's totals, and the p50/p95 latency of order
placement and of exchange acknowledgement.
