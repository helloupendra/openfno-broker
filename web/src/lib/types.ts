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
  blockedMargin: number;
  available: number;
  ledger: LedgerEntry[];
}

export interface AccountSummary {
  clientId: string;
  name: string;
  profileId: string;
  netDeposits: number;
  available: number;
  apps: number;
  liveOrders: number;
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
  feed: { quotes: number; latestTickAt: string | null };
  broker: {
    tradingDate: string;
    accounts: number;
    liveOrders: number;
    ordersToday: Partial<Record<OrderStatus, number>>;
    rejectionsToday: Record<string, number>;
    exchangeAck: Latency | null;
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
