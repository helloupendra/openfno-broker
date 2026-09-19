import { Link } from 'react-router-dom';
import { Shell } from '../components/Shell';
import { RequestTable } from '../components/Requests';
import { Pnl } from '../components/Trading';
import { Badge, Empty, ErrorNote, Live, Panel, Stat } from '../components/ui';
import { admin } from '../lib/api';
import { ago, clock, label, ms, num, rupees } from '../lib/format';
import type { Latency, OrderStatus, Overview as OverviewData, RequestEntry } from '../lib/types';
import { usePoll } from '../lib/usePoll';

const statusOrder: OrderStatus[] = ['TRANSIT', 'OPEN', 'TRIGGER_PENDING', 'PARTIALLY_FILLED', 'FILLED', 'CANCELLED', 'REJECTED', 'EXPIRED'];
const statusTone: Partial<Record<OrderStatus, string>> = {
  OPEN: 'live',
  TRANSIT: 'brand',
  TRIGGER_PENDING: 'warn',
  FILLED: 'pos',
  REJECTED: 'neg',
};

function LatencyValue({ latency }: { latency: Latency | null }) {
  if (!latency) return <span className="dim">—</span>;
  return (
    <>
      {ms(latency.p50Ms)} <span className="stat__sep">/</span> {ms(latency.p95Ms)}
    </>
  );
}

function Bars({ rows, tone }: { rows: { key: string; value: number; tone?: string }[]; tone?: string }) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  return (
    <ul className="bars">
      {rows.map((row) => (
        <li key={row.key} className="bars__row">
          <span className="bars__label">{label(row.key)}</span>
          <span className="bars__track">
            <span className={`bars__fill bars__fill--${row.tone ?? tone ?? 'brand'}`} style={{ width: `${(row.value / max) * 100}%` }} />
          </span>
          <span className="bars__value mono">{row.value}</span>
        </li>
      ))}
    </ul>
  );
}

export function Overview() {
  const overview = usePoll(() => admin.get<OverviewData>('/overview'), 3000, []);
  // The console's own back-office reads would fill the feed; show what clients sent.
  const activity = usePoll(
    () => admin.get<RequestEntry[]>('/requests?limit=200').then((all) => all.filter((r) => r.path.startsWith('/api/')).slice(0, 25)),
    2000,
    [],
  );
  const data = overview.data;

  const orders = data
    ? statusOrder
        .map((s) => ({ key: s, value: data.broker.ordersToday[s] ?? 0, tone: statusTone[s] ?? 'muted' }))
        .filter((r) => r.value > 0)
    : [];
  const totalToday = orders.reduce((sum, r) => sum + r.value, 0);
  const rejections = data
    ? Object.entries(data.broker.rejectionsToday)
        .map(([key, value]) => ({ key, value }))
        .sort((a, b) => b.value - a.value)
    : [];

  return (
    <Shell title="Overview">
      <ErrorNote error={overview.error} />
      {data && (
        <>
          <div className="stats">
            <Stat label="Accounts" value={num(data.broker.accounts)} hint={<Link to="/accounts">Open the list</Link>} />
            <Stat label="Working orders" value={num(data.broker.liveOrders)} hint="Open, waiting for a trigger, or in transit" tone="live" />
            <Stat label="Orders today" value={num(totalToday)} hint={`Trading date ${data.broker.tradingDate}`} />
            <Stat
              label="Rejected today"
              value={num(data.broker.ordersToday.REJECTED ?? 0)}
              hint="By the broker's risk checks"
              tone={(data.broker.ordersToday.REJECTED ?? 0) > 0 ? 'neg' : undefined}
            />
            <Stat
              label="Trades today"
              value={num(data.broker.tradesToday)}
              hint={`Turnover ${rupees(data.broker.turnoverToday)} · ${data.broker.openPositions} open position(s)`}
            />
            <Stat
              label="Clients' P&L today"
              value={<Pnl value={data.broker.realisedToday - data.broker.chargesToday} strong />}
              hint={`Realised ${rupees(data.broker.realisedToday)} · charges ${rupees(data.broker.chargesToday)}`}
            />
            <Stat
              label="Order call, p50 / p95"
              value={<LatencyValue latency={data.orderRequests} />}
              hint={data.orderRequests ? `Inside the broker · ${data.orderRequests.samples} calls` : 'No orders placed yet'}
            />
            <Stat
              label="Exchange ack, p50 / p95"
              value={<LatencyValue latency={data.broker.exchangeAck} />}
              hint={data.broker.exchangeAck ? `Transit to open · ${data.broker.exchangeAck.samples} orders` : 'No acknowledgements yet'}
            />
          </div>

          <div className="grid grid--3">
            <Panel title="Today's orders by status">
              {orders.length ? <Bars rows={orders} /> : <Empty>No orders today.</Empty>}
            </Panel>
            <Panel title="Why orders were refused">
              {rejections.length ? <Bars rows={rejections} tone="neg" /> : <Empty>No risk rejections today.</Empty>}
              <p className="small-note">Input errors (lot size, tick size, market orders…) create no order; see Activity.</p>
            </Panel>
            <Panel title="Markets and feed">
              <ul className="markets">
                {data.exchanges.map((x) => (
                  <li key={x.exchange} className="markets__row">
                    <span className="markets__name">{x.exchange}</span>
                    <Badge tone={x.open ? 'live' : x.holiday ? 'warn' : 'muted'}>{x.open ? 'Open' : x.holiday ? 'Holiday' : 'Closed'}</Badge>
                    <span className="dim">
                      {x.holiday ?? (x.today ? `${clock(x.today.opens)}–${clock(x.today.closes)}` : 'Not trading today')}
                    </span>
                  </li>
                ))}
              </ul>
              <div className="feed">
                <div>
                  <span className="dim">Instruments</span> <b className="mono">{num(data.instruments)}</b>
                </div>
                <div>
                  <span className="dim">Quotes</span> <b className="mono">{num(data.feed.quotes)}</b>
                </div>
                <div>
                  <span className="dim">Last tick</span> <b>{ago(data.feed.latestTickAt)}</b>
                </div>
                <div>
                  <span className="dim">Source</span>{' '}
                  <b>
                    {[data.feed.redisConfigured && 'live feed', data.feed.simulatorRunning && 'offline market'].filter(Boolean).join(' + ') ||
                      'hand-set quotes'}
                  </b>
                  {data.feed.paused && <Badge tone="warn">paused</Badge>}
                </div>
              </div>
              {data.alwaysOpen && <div className="note note--warn">Trading hours are ignored (Exchange:AlwaysOpen).</div>}
            </Panel>
          </div>
        </>
      )}

      <Panel
        title={
          <>
            <Live /> Live activity from clients
          </>
        }
        aside={<Link to="/activity">All calls</Link>}
        flush
      >
        <ErrorNote error={activity.error} />
        {activity.data && <RequestTable requests={activity.data} showClient />}
      </Panel>
    </Shell>
  );
}
