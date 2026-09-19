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
| Margin on an exit | The part of an order that closes an open position needs no margin, after allowing for other working orders already closing it | `IncreasingQuantity` | Broker RMS: an exit reduces exposure |
| Kill switch | While it is on, a new order is recorded as `REJECTED` with `KILL_SWITCH_ACTIVE`, and a modify is refused with `409`. Cancels still work | `PlaceOrderAsync`, `ModifyOrderAsync` | See section 8 |

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

## 7. What a fill costs

Charges are worked out per fill and shown per trade, per position and on the
day's contract note. Turnover is the fill's price times its quantity; for
options it is the premium turnover, not the strike value.

| Rule | Value in the FYERS profile | Enforced by | Basis |
|---|---|---|---|
| Brokerage | ₹20 per executed order, or 0.03% of its turnover if that is less. The cap is per order, so a partly filled order pays it once | `BrokerageRule.ForFill` | Source: FYERS pricing (₹20 or 0.03%, whichever is lower). **Assumed**: the same rule is used for delivery, where brokers price it differently |
| GST | 18% on brokerage, the exchange fee and the SEBI fee | `ChargeSchedule.GstPercent` | Source: GST on brokerage and exchange services |
| SEBI turnover fee | 0.0001% (₹10 per crore), both sides | `ChargeSchedule.SebiFeePercent` | **Assumed**: taken from brokers' published charge lists, not from a SEBI circular |
| Auto square-off fee | ₹50 per position closed by the broker, plus GST | `BrokerProfile.AutoSquareOffCharge` | **Assumed**: brokers charge such a fee; the amount is not from a FYERS source |

### Statutory rates by date

A trade is charged at the rates in force on its trading day
(`ChargeSchedule.RatesOn`), so a backtest of an old day is not charged today's
tax. Percentages of turnover.

| From | Category | Transaction tax (buy / sell) | Stamp duty (buy) | Exchange fee: NSE / BSE / MCX |
|---|---|---|---|---|
| 1 Oct 2024 | Equity delivery | 0.1% / 0.1% | 0.015% | 0.00297% / 0.00375% / — |
| 1 Oct 2024 | Equity intraday | — / 0.025% | 0.003% | 0.00297% / 0.00375% / — |
| 1 Oct 2024 | Equity futures | — / 0.02% | 0.002% | 0.00173% / — / — |
| 1 Oct 2024 | Equity options | — / 0.1% | 0.003% | 0.03503% / 0.0325% / — |
| 1 Oct 2024 | Commodity futures | — / 0.01% | 0.002% | — / — / 0.0021% |
| 1 Oct 2024 | Commodity options | — / 0.05% | 0.003% | — / — / 0.0418% |
| **1 Apr 2026** | Equity futures | — / **0.05%** | 0.002% | 0.00173% / — / — |
| **1 Apr 2026** | Equity options | — / **0.15%** | 0.003% | 0.03503% / 0.0325% / — |

- **Transaction tax** is STT for equity and CTT for commodities. Both are paid
  on the sell side, except equity delivery, where both sides pay.
  Source for the 1 April 2026 rates: the Finance Act 2026 (memorandum to the
  Finance Bill 2026, clause 143) and NSE circular NSE/FATAX/73524 of
  31 March 2026. Source for the October 2024 F&O rates: the Finance (No. 2)
  Act 2024. **Assumed**: agricultural commodities, which pay no CTT, are not
  separated from the rest.
- **Stamp duty** follows the uniform rates the Finance Act 2019 introduced
  from 1 July 2020: 0.015% on delivery, 0.003% on intraday and options,
  0.002% on futures, all on the buy side.
- **Exchange transaction fees** are **Assumed**: they are taken from brokers'
  published charge lists as of September 2026 and have not been checked
  against each exchange's own circular.

**Not modelled.** Exercise STT on an in-the-money option at expiry (0.15% of
intrinsic value since 1 April 2026), the depository's DP charge on delivery
sells, and any broker charge for a payment gateway, a call-and-trade or a
physical contract note.

## 8. What the broker's RMS does on its own

| Rule | Value in the FYERS profile | Enforced by | Basis |
|---|---|---|---|
| Intraday square-off | Past 15:20 IST (NSE, BSE) or 23:25 IST (MCX) the broker cancels working MIS orders and closes open MIS positions at the market | `SquareOffIntradayAsync` | **Assumed**: FYERS does not publish its square-off times; these sit 5 minutes after this profile's intraday cut-off |
| Kill switch | While it is on, every working order is cancelled and no new order is accepted (`KILL_SWITCH_ACTIVE`). Optionally every open position is closed too | `SetKillSwitchAsync` | Source: SEBI circular of 4 Feb 2025 (the investor must be able to stop their algo); broker kill-switch practice |
| Who can lift it | The client cannot turn it off before 06:00 IST the next day, which is when the session ends anyway. The back office can turn it off at any time | `SetKillSwitchAsync` | **Assumed**: the cooling-off period |

A square-off or kill-switch order is the broker's own: it carries the app id
`RMS`, no algo id, and the tag `AUTO_SQUAREOFF` or `KILL_SWITCH`. It is in the
order book and the journal like any other order, so a client can see exactly
what the broker did on its behalf.

## 9. End of day

At 23:58 IST, after every exchange has closed, the day is settled once
(`CloseTradingDayAsync`). It is idempotent, and the sandbox can run it on
demand.

| Step | What happens | Deviation from a real broker |
|---|---|---|
| Day orders | Everything still working expires | — |
| Intraday positions | Closed at the last price | The broker's RMS has normally squared them off hours earlier; this is the backstop |
| Expired contracts | Closed at the contract's last traded price; if there is none, at the option's intrinsic value from the underlying's last price | An exchange settles index options at intrinsic value against the *final settlement price* (the underlying's last half-hour average), not at the option's own last trade |
| Futures carried overnight | Marked to the last price; the difference is booked and the position carries at that price | The exchange marks to the *daily settlement price*, which is the last half-hour's weighted average |
| Delivery (CNC) | Shares bought move into holdings; shares sold leave them, and the money moves in the ledger | Real settlement is T+1. Here holdings change at the day's close, and shares bought today can be sold the same day |
| Ledger | The day's realised profit or loss and its charges are posted as one ledger entry | — |

Stock options and stock futures are cash-settled here. In the market they are
physically settled: the buyer takes delivery of the shares. A strategy that
would be assigned shares is not simulated.

## 10. Records

| Rule | What the simulator keeps | Basis |
|---|---|---|
| Audit trail of every API order with the actual user | The journal: every account, session, order and funds event, with client ID, app ID and caller IP, append-only | Source: NSE NNF 8.4.8 (audit trail at least 5 years); SEBI (Stock Brokers) Regulations 2026 (books and records, 8 years) |
| Request log | Every API call: status, refusal code and time spent per step; login bodies are never stored | Operational |

## Primary sources

- Finance Act 2026: memorandum explaining the provisions of the Finance Bill 2026, clause 143 (STT on futures 0.02% → 0.05% and on option premium 0.1% → 0.15% from 1 Apr 2026), Ministry of Finance, 1 Feb 2026; NSE circular NSE/FATAX/73524, revised STT rates, 31 Mar 2026.
- Finance (No. 2) Act 2024 (the previous rise, in force from 1 Oct 2024).
- Finance Act 2019 and the Indian Stamp (Collection of Stamp-Duty through Stock Exchanges) Rules, 2019: uniform stamp duty from 1 Jul 2020.

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
