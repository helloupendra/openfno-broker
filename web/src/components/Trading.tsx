import { istDateTime, istTime, price, rupees } from '../lib/format';
import type { ChargeBreakdown, ContractNote, Funds, Holding, Position, Trade } from '../lib/types';
import { Empty, Kv, Side, Stat } from './ui';

/** A rupee figure coloured by its sign. */
export function Pnl({ value, strong }: { value: number; strong?: boolean }) {
  const tone = value > 0 ? 'pos' : value < 0 ? 'neg' : 'dim';
  return <span className={`mono ${tone}${strong ? ' strong-pnl' : ''}`}>{rupees(value)}</span>;
}

/** Positions for a narrow column: what is held, where the market is, and the net after charges. */
function CompactPositions({ positions, actions }: { positions: Position[]; actions?: (position: Position) => React.ReactNode }) {
  const net = positions.reduce((sum, p) => sum + p.netToday, 0);
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Instrument</th>
            <th className="num">Qty</th>
            <th className="num">Avg / LTP</th>
            <th className="num" title="Realised + unrealised − charges">Net P&amp;L</th>
            {actions && <th />}
          </tr>
        </thead>
        <tbody>
          {positions.map((p) => (
            <tr key={`${p.symbol}-${p.product}`} className={p.quantity === 0 ? 'row-closed' : undefined}>
              <td>
                <div className="mono">{p.symbol}</div>
                <div className="dim small">
                  {p.product} · charges {rupees(p.chargesToday)}
                </div>
              </td>
              <td className={`num mono ${p.quantity > 0 ? 'pos' : p.quantity < 0 ? 'neg' : 'dim'}`}>{p.quantity}</td>
              <td className="num mono">
                <div>{p.quantity ? price(p.averagePrice) : '—'}</div>
                <div className="dim small">{price(p.lastPrice)}</div>
              </td>
              <td className="num">
                <Pnl value={p.netToday} strong />
                <div className="dim small">unrealised {rupees(p.unrealised)}</div>
              </td>
              {actions && <td>{actions(p)}</td>}
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <td colSpan={3} className="dim">
              Net after charges
            </td>
            <td className="num">
              <Pnl value={net} strong />
            </td>
            {actions && <td />}
          </tr>
        </tfoot>
      </table>
    </div>
  );
}

export function PositionsTable({
  positions,
  actions,
  compact,
}: {
  positions: Position[];
  actions?: (position: Position) => React.ReactNode;
  compact?: boolean;
}) {
  if (positions.length === 0) return <Empty>No positions. They appear here when an order fills.</Empty>;
  if (compact) return <CompactPositions positions={positions} actions={actions} />;
  const net = positions.reduce((sum, p) => sum + p.netToday, 0);
  const charges = positions.reduce((sum, p) => sum + p.chargesToday, 0);
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Instrument</th>
            <th>Product</th>
            <th className="num">Qty</th>
            <th className="num">Avg</th>
            <th className="num">LTP</th>
            <th className="num">Unrealised</th>
            <th className="num">Realised</th>
            <th className="num">Charges</th>
            <th className="num" title="Realised + unrealised − charges">Net P&amp;L</th>
            <th className="num">Margin</th>
            {actions && <th />}
          </tr>
        </thead>
        <tbody>
          {positions.map((p) => (
            <tr key={`${p.symbol}-${p.product}`} className={p.quantity === 0 ? 'row-closed' : undefined}>
              <td className="mono">{p.symbol}</td>
              <td className="dim">{p.product}</td>
              <td className={`num mono ${p.quantity > 0 ? 'pos' : p.quantity < 0 ? 'neg' : 'dim'}`}>{p.quantity}</td>
              <td className="num mono">{p.quantity ? price(p.averagePrice) : '—'}</td>
              <td className="num mono">{price(p.lastPrice)}</td>
              <td className="num">
                <Pnl value={p.unrealised} />
              </td>
              <td className="num">
                <Pnl value={p.realisedToday} />
              </td>
              <td className="num mono dim">{rupees(p.chargesToday)}</td>
              <td className="num">
                <Pnl value={p.netToday} strong />
              </td>
              <td className="num mono dim">{p.margin ? rupees(p.margin) : '—'}</td>
              {actions && <td>{actions(p)}</td>}
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <td colSpan={7} className="dim">
              Net since the last settlement, after charges
            </td>
            <td className="num mono dim">{rupees(charges)}</td>
            <td className="num">
              <Pnl value={net} strong />
            </td>
            <td colSpan={actions ? 2 : 1} />
          </tr>
        </tfoot>
      </table>
    </div>
  );
}

export function TradesTable({ trades }: { trades: Trade[] }) {
  if (trades.length === 0) return <Empty>No trades on this day.</Empty>;
  return (
    <div className="table-wrap">
      <table className="table table--dense">
        <thead>
          <tr>
            <th>Time</th>
            <th>Trade</th>
            <th>Order</th>
            <th>Instrument</th>
            <th>Side</th>
            <th className="num">Qty</th>
            <th className="num">Price</th>
            <th>Took</th>
            <th className="num">Charges</th>
            <th className="num">Realised</th>
          </tr>
        </thead>
        <tbody>
          {trades.map((t) => (
            <tr key={t.tradeId}>
              <td className="mono dim">{istTime(t.at, true)}</td>
              <td className="mono">{t.tradeId}</td>
              <td className="mono dim">{t.orderId}</td>
              <td>
                <span className="mono">{t.symbol}</span> <span className="dim">{t.product}</span>
                {t.tag && <span className="tag">{t.tag}</span>}
              </td>
              <td>
                <Side side={t.side} />
              </td>
              <td className="num mono">{t.quantity}</td>
              <td className="num mono">{price(t.price)}</td>
              <td className="dim">{t.maker ? 'resting' : 'market'}</td>
              <td className="num mono dim" title={chargeTitle(t.charges)}>
                {rupees(t.charges.total)}
              </td>
              <td className="num">{t.realised ? <Pnl value={t.realised} /> : <span className="dim">—</span>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function chargeTitle(c: ChargeBreakdown): string {
  return `Brokerage ${rupees(c.brokerage)} · STT/CTT ${rupees(c.transactionTax)} · Exchange ${rupees(c.exchangeFee)} · SEBI ${rupees(c.sebiFee)} · Stamp ${rupees(c.stampDuty)} · GST ${rupees(c.gst)}${c.other ? ` · Other ${rupees(c.other)}` : ''}`;
}

export function HoldingsTable({ holdings }: { holdings: Holding[] }) {
  if (holdings.length === 0) return <Empty>No holdings. Delivery (CNC) buys move here at the day's settlement.</Empty>;
  return (
    <table className="table">
      <thead>
        <tr>
          <th>Instrument</th>
          <th className="num">Qty</th>
          <th className="num">Avg</th>
          <th className="num">LTP</th>
          <th className="num">Invested</th>
          <th className="num">Value</th>
          <th className="num">P&amp;L</th>
        </tr>
      </thead>
      <tbody>
        {holdings.map((h) => (
          <tr key={h.symbol}>
            <td className="mono">{h.symbol}</td>
            <td className="num mono">{h.quantity}</td>
            <td className="num mono">{price(h.averagePrice)}</td>
            <td className="num mono">{price(h.lastPrice)}</td>
            <td className="num mono">{rupees(h.invested)}</td>
            <td className="num mono">{rupees(h.value)}</td>
            <td className="num">
              <Pnl value={h.pnl} />
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function ChargesKv({ charges }: { charges: ChargeBreakdown }) {
  return (
    <Kv
      rows={[
        ['Brokerage', rupees(charges.brokerage)],
        ['STT / CTT', rupees(charges.transactionTax)],
        ['Exchange fees', rupees(charges.exchangeFee)],
        ['SEBI fee', rupees(charges.sebiFee)],
        ['Stamp duty', rupees(charges.stampDuty)],
        ['GST', rupees(charges.gst)],
        ...(charges.other ? ([['Other (square-off fee)', rupees(charges.other)]] as [string, string][]) : []),
        [<b>Total</b>, <b className="mono">{rupees(charges.total)}</b>],
      ]}
    />
  );
}

export function ContractNoteView({ note }: { note: ContractNote }) {
  if (note.trades.length === 0) return <Empty>No trades on {note.tradingDate}, so no contract note.</Empty>;
  return (
    <div className="note-doc">
      <div className="note-doc__head">
        <div>
          <div className="note-doc__title">Contract note (simulated)</div>
          <div className="dim">
            {note.name} · <span className="mono">{note.clientId}</span> · trade date {note.tradingDate}
          </div>
        </div>
        <div className="note-doc__net">
          <span className="dim">Net after charges</span>
          <Pnl value={note.netAfterCharges} strong />
        </div>
      </div>
      <TradesTable trades={note.trades} />
      <div className="grid grid--2 note-doc__foot">
        <Kv
          rows={[
            ['Bought', rupees(note.buyValue)],
            ['Sold', rupees(note.sellValue)],
            ['Net traded value (sold − bought)', <Pnl value={note.netTradedValue} />],
            ['Charges', rupees(note.charges.total)],
            [<b>Net after charges</b>, <Pnl value={note.netAfterCharges} strong />],
          ]}
        />
        <ChargesKv charges={note.charges} />
      </div>
      <p className="small-note">
        For derivatives carried overnight, the net traded value of one day is not that day's profit: the positions tab and the ledger carry it.
      </p>
    </div>
  );
}

export function FundsStats({ funds }: { funds: Funds }) {
  return (
    <div className="stats stats--4">
      <Stat label="Available" value={rupees(funds.available)} hint="Cash less margin in use and any unrealised loss" tone="live" />
      <Stat label="Cash" value={rupees(funds.cash)} hint={`Ledger ${rupees(funds.ledgerBalance)} + today's P&L and charges`} />
      <Stat
        label="Today, after charges"
        value={<Pnl value={funds.realisedToday - funds.chargesToday} strong />}
        hint={`Realised ${rupees(funds.realisedToday)} · charges ${rupees(funds.chargesToday)}`}
      />
      <Stat
        label="Margin in use"
        value={rupees(funds.orderMargin + funds.positionMargin)}
        hint={`Orders ${rupees(funds.orderMargin)} · positions ${rupees(funds.positionMargin)} · unrealised ${rupees(funds.unrealised)}`}
        tone="warn"
      />
    </div>
  );
}

export function LedgerTable({ ledger }: { ledger: Funds['ledger'] }) {
  if (ledger.length === 0) return <Empty>No money has moved.</Empty>;
  const kinds: Record<string, string> = {
    PAY_IN: 'Pay-in',
    PAY_OUT: 'Pay-out',
    REALISED_PNL: 'Profit / loss',
    CHARGES: 'Charges',
    DELIVERY_BUY: 'Delivery buy',
    DELIVERY_SELL: 'Delivery sale',
  };
  return (
    <table className="table">
      <thead>
        <tr>
          <th>When (IST)</th>
          <th>Entry</th>
          <th>Reference</th>
          <th className="num">Amount</th>
        </tr>
      </thead>
      <tbody>
        {[...ledger].reverse().map((l) => (
          <tr key={`${l.seq}-${l.kind}-${l.reference}`}>
            <td className="mono dim">{istDateTime(l.at)}</td>
            <td>{kinds[l.kind] ?? l.kind}</td>
            <td className="dim">{l.reference}</td>
            <td className="num">
              <Pnl value={l.amount} />
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

