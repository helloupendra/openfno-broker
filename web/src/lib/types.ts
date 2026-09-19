// The broker's JSON, as the API serves it. Enums arrive as UPPER_SNAKE strings.

export type Exchange = 'NSE' | 'BSE' | 'MCX';
export type Side = 'BUY' | 'SELL';
export type OrderType = 'LIMIT' | 'MARKET' | 'STOP_LIMIT' | 'STOP_MARKET';
export type Product = 'CNC' | 'MIS' | 'NRML';
export type Validity = 'DAY' | 'IOC';
export type OrderStatus =
  | 'TRANSIT'
  | 'OPEN'
  | 'TRIGGER_PENDING'
  | 'PARTIALLY_FILLED'
  | 'FILLED'
  | 'CANCELLED'
  | 'REJECTED'
  | 'EXPIRED';

export interface HistoryEntry {
  seq: number;
  at: string;
  event: string;
  status: OrderStatus;
  note: string | null;
}

export interface Order {
  orderId: string;
  symbol: string;
  exchange: Exchange;
  segment: string;
  side: Side;
  quantity: number;
  lotSize: number;
  lots: number;
  filledQuantity: number;
  pendingQuantity: number;
  type: OrderType;
  product: Product;
  validity: Validity;
  limitPrice: number | null;
  triggerPrice: number | null;
  averagePrice: number | null;
  status: OrderStatus;
  rejectionCode: string | null;
  message: string | null;
  tag: string | null;
  algoId: string;
  appId: string;
  blockedMargin: number;
  modifications: number;
  tradingDate: string;
  placedAt: string;
  updatedAt: string;
  history: HistoryEntry[] | null;
}

export interface LedgerEntry {
  seq: number;
  at: string;
  kind: string;
  amount: number;
  reference: string;
}

export interface Funds {
  clientId: string;
  netDeposits: number;
  ledgerBalance: number;
  realisedToday: number;
  chargesToday: number;
  cash: number;
  orderMargin: number;
  positionMargin: number;
  unrealised: number;
  available: number;
  ledger: LedgerEntry[];
}

export interface ChargeBreakdown {
  brokerage: number;
  transactionTax: number;
  exchangeFee: number;
  sebiFee: number;
  stampDuty: number;
  gst: number;
  other: number;
  total: number;
}

export interface Trade {
  tradeId: string;
  orderId: string;
  clientId: string;
  symbol: string;
  exchange: Exchange;
  segment: string;
  side: Side;
  product: Product;
  quantity: number;
  price: number;
  maker: boolean;
  charges: ChargeBreakdown;
  realised: number;
  tradingDate: string;
  at: string;
  tag: string | null;
}

export interface Position {
  symbol: string;
  exchange: Exchange;
  segment: string;
  product: Product;
  quantity: number;
  averagePrice: number;
  lastPrice: number | null;
  unrealised: number;
  realisedToday: number;
  chargesToday: number;
  netToday: number;
  buyQuantity: number;
  buyAverage: number | null;
  sellQuantity: number;
  sellAverage: number | null;
  margin: number;
  updatedAt: string;
}

export interface Holding {
  symbol: string;
  exchange: Exchange;
  quantity: number;
  averagePrice: number;
  lastPrice: number | null;
  invested: number;
  value: number;
  pnl: number;
}

export interface ContractNote {
  clientId: string;
  name: string;
  tradingDate: string;
  trades: Trade[];
  buyValue: number;
  sellValue: number;
  charges: ChargeBreakdown;
  netTradedValue: number;
  netAfterCharges: number;
}

export interface KillSwitch {
  active: boolean;
  since: string | null;
  until: string | null;
  by: string | null;
  reason: string | null;
}

export interface Chaos {
  extraAckLatencyMs: number;
  exchangeRejectPercent: number;
  lostResponsePercent: number;
  unavailablePercent: number;
  feedPaused: boolean;
}

export interface SimulatedQuote {
  symbol: string;
  lastPrice: number;
  bid: number | null;
  ask: number | null;
}

export interface SimulatorStatus {
  running: boolean;
  symbols: SimulatedQuote[];
  volatilityPercent: number;
  intervalMs: number;
  spreadTicks: number;
  depthLots: number;
  steps: number;
}

export interface DayCloseReport {
  tradingDate: string;
  ordersExpired: number;
  accountsSettled: number;
}

export interface AccountSummary {
  clientId: string;
  name: string;
  profileId: string;
  netDeposits: number;
  available: number;
  realisedToday: number;
  apps: number;
  liveOrders: number;
  openPositions: number;
  killSwitch: boolean;
}

export interface RateLimits {
  orderOpsPerSecond: number;
  requestsPerSecond: number;
  requestsPerMinute: number;
  requestsPerDay: number;
  minuteBreachesAllowedPerDay: number;
}

export interface Profile {
  id: string;
  name: string;
  rateLimits: RateLimits;
  maxStaticIpsPerApp: number;
  staticIpChangesPerWeek: number;
  sessionExpiresAtIst: string;
  allowMarketOrders: boolean;
  allowIocInCommodities: boolean;
  maxModificationsPerOrder: number | null;
  intradayCutoffIst: Partial<Record<Exchange, string>>;
  margins: {
    equityIntradayPercent: number;
    indexDerivativePercent: number;
    stockDerivativePercent: number;
    commodityDerivativePercent: number;
  };
  cashPriceBandPercent: number;
  algoId: string;
}

export interface AppView {
  appId: string;
  staticIps: string[];
  staticIpChangesThisWeek: number;
  sessionExpiresAt: string | null;
}

export interface AccountView {
  clientId: string;
  name: string;
  openedAt: string;
  profile: Profile;
  apps: AppView[];
}

export interface OpenedAccount {
  clientId: string;
  totpSecret: string;
  totpUri: string;
}

export interface RegisteredApp {
  clientId: string;
  appId: string;
  appSecret: string;
  staticIps: string[];
}

export interface SessionGrant {
  accessToken: string;
  clientId: string;
  appId: string;
  expiresAt: string;
}

export interface RequestEntry {
  at: string;
  clientId: string | null;
  appId: string | null;
  clientIp: string | null;
  method: string;
  path: string;
  status: number;
  errorCode: string | null;
  orderId: string | null;
  totalMs: number;
  authMs: number | null;
  rateLimitMs: number | null;
  queueMs: number | null;
  decideMs: number | null;
  journalMs: number | null;
  body: string | null;
}

export interface Latency {
  samples: number;
  p50Ms: number;
  p95Ms: number;
  maxMs: number;
}

export interface Session {
  opens: string;
  closes: string;
}

export interface ExchangeStatus {
  exchange: Exchange;
  open: boolean;
  today: Session | null;
  holiday: string | null;
}

export interface Overview {
  now: string;
  alwaysOpen: boolean;
  exchanges: ExchangeStatus[];
  instruments: number;
  feed: {
    quotes: number;
    latestTickAt: string | null;
    redisConfigured: boolean;
    redisTicks: number;
    simulatorRunning: boolean;
    paused: boolean;
  };
  broker: {
    tradingDate: string;
    accounts: number;
    liveOrders: number;
    ordersToday: Partial<Record<OrderStatus, number>>;
    rejectionsToday: Record<string, number>;
    exchangeAck: Latency | null;
    tradesToday: number;
    turnoverToday: number;
    chargesToday: number;
    realisedToday: number;
    openPositions: number;
    lastClosedDate: string | null;
    lastSeq: number;
  };
  orderRequests: Latency | null;
}

export interface JournalEvent {
  event: string;
  seq: number;
  at: string;
  clientId: string;
  [field: string]: unknown;
}

export interface Instrument {
  symbol: string;
  exchange: Exchange;
  segment: string;
  kind: string;
  underlying: string;
  underlyingClass: string;
  exchangeToken: string | null;
  lotSize: number;
  tickSize: number;
  freezeQuantity: number | null;
  expiry: string | null;
  strike: number | null;
  right: string | null;
  session: Session;
  isTradable: boolean;
}

export interface Quote {
  symbol: string;
  lastPrice: number;
  at: string;
  bid: number | null;
  ask: number | null;
  previousClose: number | null;
}

export interface Holiday {
  exchange: Exchange;
  date: string;
  name: string;
  closure: 'FULL_DAY' | 'MORNING_SESSION' | 'EVENING_SESSION';
  source: string;
}

export interface SpecialSession {
  exchange: Exchange;
  date: string;
  name: string;
  opens: string;
  closes: string;
  source: string;
}

export interface Calendar {
  holidays: Holiday[];
  specialSessions: SpecialSession[];
}
