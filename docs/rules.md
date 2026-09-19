# The rules the broker enforces

Every rule below is enforced in code, and each one names the code that does
it. Each rule is marked:

- **Source**: a primary text (SEBI or exchange circular, broker documentation).
- **Assumed**: a value we chose because no primary text was found. It is flagged
  so it can be replaced when one is.

The regulatory position is as of September 2026. Since 1 April 2026 the SEBI
retail-algo framework applies to every broker in India. Under it, every API
order counts as an algo order.

Where the simulator deliberately differs from the real broker, the row says so
under **Deviation**.

## 1. Access

| Rule | Value in the FYERS profile | Enforced by | Basis |
|---|---|---|---|
| Orders only from a whitelisted static IP | Place, modify and cancel from an app's registered IP; reads from anywhere | `StaticIpFilter` | Source: NSE/INVG/67858 (5 May 2025); FYERS notice, 10 Mar 2026: "Orders will only be accepted from a registered App ID mapped to a whitelisted static IP"; Zerodha: data, order book and positions from any IP |
| IPs per app | 1 ("One app, one IP") | `BrokerProfile.MaxStaticIpsPerApp` | Source: FYERS notice, 10 Mar 2026 |
| Changing the static IP | At most once per calendar week (Monday to Sunday, IST) | `ChangeStaticIpsAsync` | Source: NSE/INVG/67858; NSE FAQ, 3 Nov 2025 |
| Two-factor login every day | TOTP (RFC 6238) at login; no refresh tokens | `LoginAsync`, `Totp` | Source: SEBI circular of 4 Feb 2025 (OAuth + 2FA); FYERS: "2FA once every trading day", refresh tokens discontinued from 1 Apr 2026 |
| Sessions end before the next trading day | 06:00 IST after login | `BrokerProfile.SessionExpiresAt` | Source for the rule: NSE/INVG/67858 ("logged out every day before the start of the next trading day"). **Assumed**: the 06:00 cut-off time |
| A TOTP code works once | A code from an already-used 30-second step is refused | `Totp.Verify` | Standard TOTP replay protection |

**Deviation (login).** Real brokers log in through OAuth: a browser redirect
followed by an auth-code exchange. The simulator takes the app ID, app secret,
client ID and TOTP in a single call, `POST /api/v1/session`. The checks are
the same; only the number of round trips differs.

## 2. Order rate

| Rule | Value in the FYERS profile | Enforced by | Basis |
|---|---|---|---|
| Order operations per second | 10. Place, modify and cancel are counted together | `RateLimiter` | Source: NSE/INVG/67858 (10 OPS threshold, measured on the broker server's calendar second); FYERS rate-limit reference |
| All requests | 10 per second, 200 per minute, 1,00,000 per day | `RateLimiter` | Source: FYERS API v3 rate limits |
| Repeated breaches | More than 3 minutes in a day at the per-minute limit blocks the client until midnight IST | `RateLimiter` | Source: FYERS API v3 rate limits |
| Refused requests | Answered 429 with `Retry-After`; a refused request is not counted | `RateLimitFilter` | Source: FYERS (429 with Retry-After) |

The per-second window is the calendar second, not a rolling one, as the NSE
standard specifies. The limit applies per client across all exchanges. That is
stricter than the per-exchange threshold, so a strategy that fits here fits
the rule.

## 3. Algo tagging

| Rule | Value | Enforced by | Basis |
|---|---|---|---|
| Every API order is an algo order and is tagged | Algo ID `99999` on every order (unregistered client algo within 10 OPS) | `OrderTicket.AlgoId` | Source: NSE FAQ Q8 (3 Nov 2025); NSE consolidated NNF circular NSE/INVG/73992, 8.4.7 |

Registered algos above 10 OPS, which get exchange-assigned IDs, are out of
scope.

## 4. Order types

| Rule | Value in the FYERS profile | Enforced by | Basis |
|---|---|---|---|
| No market orders for algo orders | `MARKET` and `STOP_MARKET` are refused (`MARKET_ORDER_NOT_ALLOWED`) | `OrderRules.CheckShape` | Source: NSE NNF circular 8.1.12 ("Algo orders with order type as Market Order are not permitted"); exchange penalty of ₹1,000 per market algo order (NSE/SURV/57315) |
| No IOC for algo orders in commodities | `IOC` refused in the commodity segment | `OrderRules.CheckShape` | Source: NSE NNF circular 8.2.1; Zerodha on MCX |
| No after-market orders through the API | Orders outside the trading session are refused (`MARKET_CLOSED`) | `OrderRules.CheckMarketOpen` | Source: FYERS notice, 10 Mar 2026 (AMO through the API prohibited) |
| Modifications per order | No limit (FYERS publishes none) | `BrokerProfile.MaxModificationsPerOrder` | Zerodha and Dhan publish 25; FYERS is left unlimited until a source is found |

**Deviation (market orders).** FYERS does not reject a market order. It
converts it to a Market Price Protection (MPP) limit order. The simulator
rejects it instead, for two reasons: we have not found the width of FYERS's
protection band in a primary source, and the engine this broker serves should
never send market orders in the first place. See
[decision 0004](decisions/0004-reject-market-orders.md).

## 5. Contract rules

| Rule | Value | Enforced by | Basis |
|---|---|---|---|
| Quantity in whole lots | `quantity % lotSize == 0` | `OrderRules.CheckShape` | Exchange contract specifications, from the FYERS symbol master |
| Quantity freeze | Orders at or above the exchange freeze quantity are refused; the client slices them | `OrderRules.CheckShape` | Exchange freeze limits, from Dhan's detailed scrip master (`SM_FREEZE_QTY`) |
| Price on the tick grid | Limit and trigger prices must be multiples of the tick size | `OrderRules.CheckPrices` | Exchange tick sizes, from the FYERS symbol master |
| Stop-limit geometry | A buy stop triggers above the last price and at or below its limit; a sell stop the reverse | `OrderRules.CheckPrices` | Exchange order validation |
| Products by segment | CNC for cash only; NRML for derivatives and commodities only; MIS everywhere | `OrderRules.CheckShape` | Broker product definitions |
| Expired contracts | Refused after expiry day | `OrderRules.CheckInstrument` | — |
| Indices | Not tradable | `OrderRules.CheckInstrument` | — |
| Trading hours | The instrument's session from the symbol master, on trading days only | `ExchangeCalendar` | Source: holiday circulars in `data/calendar/2026.json` (each row names its circular) |

**Freeze quantity semantics.** The simulator reads a freeze quantity as the
smallest quantity that is refused. Under the other reading, the largest
quantity that is allowed, the two differ only when the freeze is an exact
multiple of the lot size. We picked the reading that never accepts an order
the exchange might refuse.

**Commodity lots.** Neither vendor master carries MCX lot sizes. Both report
1, because their commodity quantities are counted in lots. Lot sizes come from
`data/reference/commodity-lot-sizes.json`. A commodity missing from that file
cannot be traded (`INSTRUMENT_NOT_CONFIGURED`); this is better than trading it
at a lot of 1. The Dhan freeze quantity for commodities is also in lots, and
is converted to units.

## 6. Risk checks (order recorded as REJECTED)

| Rule | Value in the FYERS profile | Enforced by | Basis |
|---|---|---|---|
| Margin | The order's margin must fit the available funds. The margin is blocked while the order works and released on cancel or expiry | `Margin`, `OrderRules.CheckRisk` | See the margin table below |
| Delivery sells need holdings | A CNC sell needs settled shares | `OrderRules.CheckRisk` | Broker RMS |
| Cash price band | A limit price more than 20% from the previous close (or the last price) is refused | `OrderRules.CheckRisk` | 20% is the widest NSE equity circuit. **Assumed**: stock-specific bands (2, 5, 10%) and derivative operating ranges are not modelled yet |
| Intraday cut-off | No new MIS orders after 15:15 (NSE and BSE) or 23:00 (MCX); existing orders can still be modified | `OrderRules.CheckRisk` | **Assumed**: FYERS square-off times not verified from a primary source |

### Margin model

| Position | Margin blocked |
|---|---|
| Delivery (CNC) buy | 100% of value |
| Intraday (MIS) equity | 20% of value (SEBI's peak-margin rules cap intraday leverage at 5x) |
| Option buy | The full premium |
| Option sell | 12% (index) / 20% (stock) / 25% (commodity) of strike × quantity |
| Future | 12% (index) / 20% (stock) / 25% (commodity) of price × quantity |

This is a simplification of SPAN plus exposure margin, and **Assumed**. The
rates lean high on purpose, so a strategy that fits the simulator's margin
fits a real broker's. Computing real SPAN from the exchanges' risk-parameter
files is on the roadmap.

## 7. Records

| Rule | What the simulator keeps | Basis |
|---|---|---|
| Audit trail of every API order with the actual user | The journal: every account, session, order and funds event, with client ID, app ID and caller IP, append-only | Source: NSE NNF 8.4.8 (audit trail at least 5 years); SEBI (Stock Brokers) Regulations 2026 (books and records, 8 years) |
| Request log | Every API call: status, refusal code and time spent per step; login bodies are never stored | Operational |

## Primary sources

- SEBI circular SEBI/HO/MIRSD/MIRSD-PoD/P/CIR/2025/0000013, *Safer participation of retail investors in algorithmic trading*, 4 Feb 2025. https://www.sebi.gov.in/legal/circulars/feb-2025/safer-participation-of-retail-investors-in-algorithmic-trading_91614.html
- SEBI circular SEBI/HO/MIRSD/MIRSD-PoD/P/CIR/2025/132, 30 Sep 2025. It moved the framework to all brokers from 1 Apr 2026. https://www.sebi.gov.in/legal/circulars/sep-2025/extension-of-timeline-for-implementation-of-sebi-circular-dated-february-04-2025-on-safer-participation-of-retail-investors-in-algorithmic-trading-_96979.html
- NSE/INVG/67858, *Implementation standards*, 5 May 2025. https://nsearchives.nseindia.com/content/circulars/INVG67858.pdf
- NSE FAQ on retail algo trading, 3 Nov 2025. https://nsearchives.nseindia.com/web/sites/default/files/inline-files/FAQ_Retail%20Algo_03112025_NSE.pdf
- NSE/INVG/73992, consolidated NNF circular, 30 Apr 2026, sections 8.1.12, 8.2.1, 8.4.6–8.4.8.
- NSE/SURV/74008, penalty schedule, 30 Apr 2026. https://nsearchives.nseindia.com/content/circulars/SURV74008.pdf
- FYERS notice, *New SEBI framework for retail algo trading from April 01, 2026*, 10 Mar 2026. https://fyers.in/notice-board/new-sebi-framework-for-retail-algo-trading-from-april-01-2026/
- FYERS API v3 rate limits. https://github.com/FyersDev/fyers-skills/blob/master/skills/fyers-trading/references/rate-limits.md
- Zerodha, static IP for Kite Connect. https://support.zerodha.com/category/trading-and-markets/general-kite/kite-api/articles/static-ip
- Exchange holiday circulars: the `source` field of every row in `data/calendar/2026.json`.
