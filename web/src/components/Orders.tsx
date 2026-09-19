import { useState } from 'react';
import { istTime, label, ms, price, rupees } from '../lib/format';
import type { HistoryEntry, Order } from '../lib/types';
import { Drawer, Empty, Kv, Side, StatusBadge } from './ui';

function priceCell(order: Order) {
  if (order.type === 'STOP_LIMIT' || order.type === 'STOP_MARKET')
    return (
      <>
        {price(order.limitPrice)}
        <span className="dim price-trigger"> @ {price(order.triggerPrice)}</span>
      </>
    );
  return price(order.limitPrice);
}

/** A narrow order book for a side column: what, at what price, and where it stands. */
function CompactOrderTable({
  orders,
  onOpen,
  actions,
}: {
  orders: Order[];
  onOpen: (order: Order) => void;
  actions?: (order: Order) => React.ReactNode;
}) {
  return (
    <div className="table-wrap">
      <table className="table table--hover table--compact">
        <thead>
          <tr>
            <th>Time</th>
            <th>Order</th>
            <th className="num">Price</th>
            <th>Status</th>
            {actions && <th />}
          </tr>
        </thead>
        <tbody>
          {orders.map((order) => (
            <tr key={order.orderId} onClick={() => onOpen(order)}>
              <td className="mono dim">{istTime(order.placedAt)}</td>
              <td>
                <div>
                  <Side side={order.side} /> <span className="mono">{order.quantity}</span> <span className="mono">{order.symbol}</span>
                  {order.tag && <span className="tag">{order.tag}</span>}
                </div>
                <div className="dim small">
                  {label(order.type)} · {order.product}
                  {order.validity === 'IOC' && ' · IOC'} · <span className="mono">{order.orderId}</span>
                </div>
              </td>
              <td className="num mono">{priceCell(order)}</td>
              <td>
                <StatusBadge status={order.status} />
                {order.rejectionCode && <div className="reason">{order.rejectionCode}</div>}
              </td>
              {actions && <td onClick={(e) => e.stopPropagation()}>{actions(order)}</td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** An order book. Clicking a row opens the order's full history. */
export function OrderTable({
  orders,
  onOpen,
  actions,
  compact,
  empty = 'No orders on this day.',
}: {
  orders: Order[];
  onOpen: (order: Order) => void;
  actions?: (order: Order) => React.ReactNode;
  compact?: boolean;
  empty?: string;
}) {
  if (orders.length === 0) return <Empty>{empty}</Empty>;
  if (compact) return <CompactOrderTable orders={orders} onOpen={onOpen} actions={actions} />;
  return (
    <div className="table-wrap">
      <table className="table table--hover table--compact">
        <thead>
          <tr>
            <th>Time</th>
            <th>Order</th>
            <th>Instrument</th>
            <th>Side</th>
            <th className="num">Qty</th>
            <th>Type</th>
            <th className="num">Price</th>
            <th>Status</th>
            <th className="num">Margin</th>
            {actions && <th />}
          </tr>
        </thead>
        <tbody>
          {orders.map((order) => (
            <tr key={order.orderId} onClick={() => onOpen(order)}>
              <td className="mono dim">{istTime(order.placedAt)}</td>
              <td className="mono">{order.orderId}</td>
              <td>
                <span className="mono">{order.symbol}</span>
                {order.tag && <span className="tag">{order.tag}</span>}
              </td>
              <td>
                <Side side={order.side} />
              </td>
              <td className="num mono">
                {order.quantity}
                {order.lotSize > 1 && <span className="dim"> · {order.lots}L</span>}
              </td>
              <td className="dim">
                {label(order.type)} · {order.product} {order.validity === 'IOC' && '· IOC'}
              </td>
              <td className="num mono">{priceCell(order)}</td>
              <td>
                <StatusBadge status={order.status} />
                {order.rejectionCode && <div className="reason">{order.rejectionCode}</div>}
              </td>
              <td className="num mono">{order.blockedMargin ? rupees(order.blockedMargin) : <span className="dim">—</span>}</td>
              {actions && <td onClick={(e) => e.stopPropagation()}>{actions(order)}</td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Timeline({ history }: { history: HistoryEntry[] }) {
  return (
    <ol className="timeline">
      {history.map((entry, i) => {
        const previous = history[i - 1];
        const delta = previous ? new Date(entry.at).getTime() - new Date(previous.at).getTime() : null;
        return (
          <li key={entry.seq} className={`timeline__item timeline__item--${entry.event.includes('rejected') ? 'neg' : 'ok'}`}>
            <div className="timeline__head">
              <span className="timeline__event">{label(entry.event)}</span>
              <StatusBadge status={entry.status} />
              <span className="timeline__time mono">{istTime(entry.at, true)}</span>
              {delta !== null && <span className="timeline__delta mono">+{ms(delta)}</span>}
            </div>
            {entry.note && <p className="timeline__note">{entry.note}</p>}
            <span className="timeline__seq mono">journal #{entry.seq}</span>
          </li>
        );
      })}
    </ol>
  );
}

/** One order: what it asked for, what the broker did, and when each step happened. */
export function OrderDrawer({ order, onClose }: { order: Order; onClose: () => void }) {
  const [raw, setRaw] = useState(false);
  return (
    <Drawer
      title={
        <>
          Order <span className="mono">{order.orderId}</span>
        </>
      }
      onClose={onClose}
    >
      <div className="drawer__summary">
        <Side side={order.side} />
        <span className="mono big">{order.symbol}</span>
        <StatusBadge status={order.status} />
      </div>
      {order.message && (
        <div className={order.status === 'REJECTED' ? 'note note--neg' : 'note'}>
          {order.rejectionCode && <code>{order.rejectionCode}</code>} {order.message}
        </div>
      )}
      <Kv
        rows={[
          ['Quantity', `${order.quantity} (${order.lots} lot${order.lots === 1 ? '' : 's'} of ${order.lotSize})`],
          ['Filled / pending', `${order.filledQuantity} / ${order.pendingQuantity}`],
          ['Type', `${label(order.type)} · ${order.product} · ${order.validity}`],
          ['Limit price', price(order.limitPrice)],
          ['Trigger price', price(order.triggerPrice)],
          ['Margin blocked', rupees(order.blockedMargin)],
          ['Modifications', order.modifications],
          ['Algo ID', <span className="mono">{order.algoId}</span>],
          ['App', <span className="mono">{order.appId}</span>],
          ['Tag', order.tag ?? '—'],
          ['Trading date', order.tradingDate],
        ]}
      />
      <h3 className="section-title">What happened, and when</h3>
      {order.history ? <Timeline history={order.history} /> : <Empty>Loading history…</Empty>}
      <button className="btn btn--ghost btn--sm" onClick={() => setRaw(!raw)}>
        {raw ? 'Hide' : 'Show'} JSON
      </button>
      {raw && <pre className="json">{JSON.stringify(order, null, 2)}</pre>}
    </Drawer>
  );
}
