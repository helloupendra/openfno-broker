import { Fragment, useState } from 'react';
import { istTime, millis, ms, rupees } from '../lib/format';
import type { JournalEvent, RequestEntry } from '../lib/types';
import { Badge, Empty, HttpStatus } from './ui';

function percentile(values: number[], p: number): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(p * sorted.length) - 1))] ?? null;
}

/** p50 and p95 of the total time of the calls shown. */
export function LatencySummary({ requests }: { requests: RequestEntry[] }) {
  const orders = requests.filter((r) => r.path.startsWith('/api/v1/orders') && r.method !== 'GET').map((r) => r.totalMs);
  const all = requests.map((r) => r.totalMs);
  return (
    <div className="latency">
      <span>
        Order calls <b className="mono">{ms(percentile(orders, 0.5))}</b> p50 · <b className="mono">{ms(percentile(orders, 0.95))}</b> p95
      </span>
      <span className="dim">
        All calls {ms(percentile(all, 0.5))} p50 · {ms(percentile(all, 0.95))} p95 · {requests.length} shown
      </span>
    </div>
  );
}

/** Where each call's time went: auth, rate limit, waiting for the engine, deciding, journaling. */
export function RequestTable({ requests, showClient }: { requests: RequestEntry[]; showClient?: boolean }) {
  const [open, setOpen] = useState<number | null>(null);
  if (requests.length === 0) return <Empty>No calls yet.</Empty>;
  const columns = showClient ? 12 : 11;
  return (
    <div className="table-wrap">
      <table className="table table--hover table--dense">
        <thead>
          <tr>
            <th>Time</th>
            {showClient && <th>Client</th>}
            <th>Call</th>
            <th>Status</th>
            <th>Code</th>
            <th className="num" title="Milliseconds inside the broker">Total ms</th>
            <th className="num" title="Resolving the access token">Auth</th>
            <th className="num" title="The rate limiter">Rate</th>
            <th className="num" title="Waiting for the engine's lock">Queue</th>
            <th className="num" title="Rules and margin">Decide</th>
            <th className="num" title="Writing the journal">Journal</th>
            <th>From</th>
          </tr>
        </thead>
        <tbody>
          {requests.map((r, i) => (
            <Fragment key={`${r.at}-${i}`}>
              <tr onClick={() => setOpen(open === i ? null : i)}>
                <td className="mono dim">{istTime(r.at, true)}</td>
                {showClient && <td className="mono">{r.clientId ?? <span className="dim">—</span>}</td>}
                <td className="mono">
                  <span className={`method method--${r.method.toLowerCase()}`}>{r.method}</span> {r.path}
                </td>
                <td>
                  <HttpStatus status={r.status} />
                </td>
                <td>{r.errorCode ? <span className={r.status >= 400 ? 'code code--neg' : 'code'}>{r.errorCode}</span> : <span className="dim">—</span>}</td>
                <td className="num mono strong">{millis(r.totalMs)}</td>
                <td className="num mono dim">{millis(r.authMs)}</td>
                <td className="num mono dim">{millis(r.rateLimitMs)}</td>
                <td className="num mono dim">{millis(r.queueMs)}</td>
                <td className="num mono dim">{millis(r.decideMs)}</td>
                <td className="num mono dim">{millis(r.journalMs)}</td>
                <td className="mono dim">{r.clientIp ?? '—'}</td>
              </tr>
              {open === i && (
                <tr className="row-detail">
                  <td colSpan={columns}>
                    {r.orderId && (
                      <div>
                        Order <span className="mono">{r.orderId}</span>
                      </div>
                    )}
                    {r.body ? <pre className="json">{pretty(r.body)}</pre> : <span className="dim">No body kept for this call.</span>}
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function pretty(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

const eventTone: Record<string, string> = {
  'order.rejected': 'neg',
  'order.amend_rejected': 'neg',
  'order.placed': 'brand',
  'order.accepted': 'live',
  'order.cancelled': 'muted',
  'order.expired': 'muted',
  'order.modified': 'warn',
  'funds.added': 'pos',
  'funds.withdrawn': 'warn',
  'order.filled': 'pos',
  'order.triggered': 'warn',
  'order.exchange_rejected': 'neg',
  'account.kill_switch': 'neg',
  'account.day_settled': 'brand',
};

/** A one-line account of an event, for the journal list. */
function describe(e: JournalEvent): string {
  const ticket = e.ticket as { side?: string; quantity?: number; symbol?: string; limitPrice?: number } | undefined;
  switch (e.event) {
    case 'account.opened':
      return `Account opened for ${String(e.name)} on the ${String(e.profileId)} profile`;
    case 'funds.added':
      return `Pay-in of ${rupees(Number(e.amount))} (${String(e.reference)})`;
    case 'funds.withdrawn':
      return `Pay-out of ${rupees(Number(e.amount))} (${String(e.reference)})`;
    case 'app.registered':
      return `App ${String(e.appId)} issued; static IPs: ${(e.staticIps as string[]).join(', ') || 'none'}`;
    case 'app.static_ips_changed':
      return `App ${String(e.appId)} static IPs now ${(e.staticIps as string[]).join(', ')}`;
    case 'session.opened':
      return `Logged in with app ${String(e.appId)} from ${String(e.clientIp ?? 'unknown')}`;
    case 'session.closed':
      return `Session ended: ${String(e.reason)}`;
    case 'order.placed':
      return `${ticket?.side} ${ticket?.quantity} ${ticket?.symbol} @ ${ticket?.limitPrice ?? 'market'} sent to the exchange`;
    case 'order.rejected':
      return `${ticket?.side} ${ticket?.quantity} ${ticket?.symbol} rejected: ${String(e.code)}`;
    case 'order.accepted':
      return `Order ${String(e.orderId)} acknowledged: ${String(e.status)}`;
    case 'order.modified':
      return `Order ${String(e.orderId)} modified: qty ${String(e.quantity)}, ${String(e.type)}${e.limitPrice ? ` @ ${String(e.limitPrice)}` : ''}`;
    case 'order.amend_rejected':
      return `${String(e.action)} of ${String(e.orderId)} refused: ${String(e.code)}`;
    case 'order.cancelled':
      return `Order ${String(e.orderId)} cancelled: ${String(e.reason)}`;
    case 'order.expired':
      return `Order ${String(e.orderId)} expired at the session close`;
    case 'order.triggered':
      return `Order ${String(e.orderId)} triggered at ${String(e.lastPrice)}`;
    case 'order.filled': {
      const charges = (e.charges as { total?: number } | undefined)?.total ?? 0;
      return `Order ${String(e.orderId)}: ${String(e.quantity)} traded @ ${String(e.price)}, charges ${rupees(charges)}${Number(e.realised) ? `, booked ${rupees(Number(e.realised))}` : ''}`;
    }
    case 'order.exchange_rejected':
      return `Order ${String(e.orderId)} rejected by the exchange: ${String(e.code)}`;
    case 'account.kill_switch':
      return e.active ? `Kill switch on (${String(e.by)}): ${String(e.reason)}` : `Kill switch off (${String(e.by)})`;
    case 'account.day_settled':
      return `Day ${String(e.tradingDate)} settled: P&L ${rupees(Number(e.realised))}, charges ${rupees(Number(e.charges))}, ${(e.entries as unknown[]).length} settlement line(s)`;
    default:
      return e.event;
  }
}

export function JournalList({ events }: { events: JournalEvent[] }) {
  const [open, setOpen] = useState<number | null>(null);
  if (events.length === 0) return <Empty>Nothing recorded yet.</Empty>;
  return (
    <ul className="journal">
      {[...events].reverse().map((e) => (
        <li key={e.seq} className="journal__item" onClick={() => setOpen(open === e.seq ? null : e.seq)}>
          <span className="journal__seq mono">#{e.seq}</span>
          <span className="journal__time mono">{istTime(e.at, true)}</span>
          <Badge tone={eventTone[e.event] ?? 'muted'}>{e.event}</Badge>
          <span className="journal__text">{describe(e)}</span>
          {open === e.seq && <pre className="json journal__json">{JSON.stringify(e, null, 2)}</pre>}
        </li>
      ))}
    </ul>
  );
}
