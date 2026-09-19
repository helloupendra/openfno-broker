import { useEffect, useState } from 'react';
import { Shell } from '../components/Shell';
import { Badge, ErrorNote, Field, Kv, Live, Panel } from '../components/ui';
import { admin, ApiError } from '../lib/api';
import { ago, istToday, num, price } from '../lib/format';
import type { Chaos, DayCloseReport, Overview, SimulatorStatus } from '../lib/types';
import { usePoll } from '../lib/usePoll';

interface SymbolRow {
  symbol: string;
  startPrice: string;
}

function Simulator() {
  const status = usePoll(() => admin.get<SimulatorStatus>('/simulator'), 2000, []);
  const [rows, setRows] = useState<SymbolRow[]>([{ symbol: 'NSE:SBIN-EQ', startPrice: '800' }]);
  const [volatility, setVolatility] = useState('0.05');
  const [interval, setIntervalMs] = useState('1000');
  const [spread, setSpread] = useState('2');
  const [depth, setDepth] = useState('20');
  const [error, setError] = useState<ApiError | null>(null);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    const s = status.data;
    if (!s || loaded) return;
    setLoaded(true);
    if (s.symbols.length > 0) setRows(s.symbols.map((q) => ({ symbol: q.symbol, startPrice: '' })));
    setVolatility(String(s.volatilityPercent || 0.05));
    setIntervalMs(String(s.intervalMs || 1000));
    setSpread(String(s.spreadTicks || 2));
    setDepth(String(s.depthLots || 20));
  }, [status.data, loaded]);

  async function apply(running: boolean) {
    setError(null);
    try {
      await admin.put<SimulatorStatus>('/simulator', {
        running,
        symbols: rows
          .filter((r) => r.symbol.trim())
          .map((r) => ({ symbol: r.symbol.trim().toUpperCase(), startPrice: r.startPrice ? Number(r.startPrice) : null })),
        volatilityPercent: Number(volatility),
        intervalMs: Number(interval),
        spreadTicks: Number(spread),
        depthLots: Number(depth),
      });
      status.reload();
    } catch (err) {
      setError(err as ApiError);
    }
  }

  const s = status.data;
  return (
    <Panel
      title={
        <>
          <Live on={!!s?.running} /> Offline market
        </>
      }
      aside={s && <span className="dim">{s.running ? `Running · ${num(s.steps)} steps` : 'Stopped'}</span>}
    >
      <p className="small-note">
        A random walk per symbol with a bid, an ask and depth, written into the quote book as if the feed had sent it: orders trigger and fill
        with the markets closed. Leave a start price empty to begin from the symbol's last quote. Keep it off the symbols the live feed prices.
      </p>
      <div className="sim-rows">
        {rows.map((row, i) => (
          <div className="sim-row" key={i}>
            <input
              className="input mono"
              placeholder="NSE:SBIN-EQ"
              value={row.symbol}
              onChange={(e) => setRows(rows.map((r, j) => (j === i ? { ...r, symbol: e.target.value } : r)))}
            />
            <input
              className="input mono"
              placeholder="Start price"
              inputMode="decimal"
              value={row.startPrice}
              onChange={(e) => setRows(rows.map((r, j) => (j === i ? { ...r, startPrice: e.target.value } : r)))}
            />
            <button className="btn btn--ghost btn--sm" onClick={() => setRows(rows.filter((_, j) => j !== i))} aria-label="Remove">
              ✕
            </button>
          </div>
        ))}
        <button className="link" onClick={() => setRows([...rows, { symbol: '', startPrice: '' }])}>
          + Add a symbol
        </button>
      </div>
      <div className="form__row">
        <Field label="Move per step (%)" hint="Standard deviation">
          <input className="input mono" value={volatility} onChange={(e) => setVolatility(e.target.value)} />
        </Field>
        <Field label="Step (ms)">
          <input className="input mono" value={interval} onChange={(e) => setIntervalMs(e.target.value)} />
        </Field>
        <Field label="Spread (ticks)">
          <input className="input mono" value={spread} onChange={(e) => setSpread(e.target.value)} />
        </Field>
        <Field label="Depth (lots)">
          <input className="input mono" value={depth} onChange={(e) => setDepth(e.target.value)} />
        </Field>
      </div>
      <ErrorNote error={error} />
      <div className="form__actions">
        {s?.running && (
          <button className="btn btn--ghost" onClick={() => void apply(false)}>
            Stop
          </button>
        )}
        <button className="btn btn--primary" onClick={() => void apply(true)}>
          {s?.running ? 'Apply' : 'Start the market'}
        </button>
      </div>
      {s && s.symbols.length > 0 && (
        <table className="table table--dense sim-quotes">
          <thead>
            <tr>
              <th>Symbol</th>
              <th className="num">Bid</th>
              <th className="num">Last</th>
              <th className="num">Ask</th>
            </tr>
          </thead>
          <tbody>
            {s.symbols.map((q) => (
              <tr key={q.symbol}>
                <td className="mono">{q.symbol}</td>
                <td className="num mono">{price(q.bid)}</td>
                <td className="num mono strong">{price(q.lastPrice)}</td>
                <td className="num mono">{price(q.ask)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  );
}

function ChaosPanel() {
  const chaos = usePoll(() => admin.get<Chaos>('/chaos'), 0, []);
  const [draft, setDraft] = useState<Chaos | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [saved, setSaved] = useState(false);
  useEffect(() => {
    if (chaos.data) setDraft(chaos.data);
  }, [chaos.data]);

  async function save(next: Chaos) {
    setError(null);
    try {
      setDraft(await admin.put<Chaos>('/chaos', next));
      setSaved(true);
      window.setTimeout(() => setSaved(false), 1500);
    } catch (err) {
      setError(err as ApiError);
    }
  }

  if (!draft) return <Panel title="Chaos mode">Loading…</Panel>;
  const set = (key: keyof Chaos, value: number | boolean) => setDraft({ ...draft, [key]: value });
  const on = draft.extraAckLatencyMs > 0 || draft.exchangeRejectPercent > 0 || draft.lostResponsePercent > 0 || draft.unavailablePercent > 0 || draft.feedPaused;

  return (
    <Panel title="Chaos mode" aside={on ? <Badge tone="warn">Faults on</Badge> : <Badge>Off</Badge>}>
      <p className="small-note">
        Faults a real broker has on a bad day, so a client's error handling meets them here first. They apply to every account.
      </p>
      <div className="form">
        <Field label="Extra exchange latency (ms)" hint="Added to every acknowledgement; orders stay in transit longer">
          <input className="input mono" value={draft.extraAckLatencyMs} onChange={(e) => set('extraAckLatencyMs', Number(e.target.value) || 0)} />
        </Field>
        <div className="form__row">
          <Field label="Exchange rejects (%)" hint="Rejected after acknowledgement">
            <input className="input mono" value={draft.exchangeRejectPercent} onChange={(e) => set('exchangeRejectPercent', Number(e.target.value) || 0)} />
          </Field>
          <Field label="Lost responses (%)" hint="Done, but answered 504">
            <input className="input mono" value={draft.lostResponsePercent} onChange={(e) => set('lostResponsePercent', Number(e.target.value) || 0)} />
          </Field>
          <Field label="Broker down (%)" hint="503, nothing done">
            <input className="input mono" value={draft.unavailablePercent} onChange={(e) => set('unavailablePercent', Number(e.target.value) || 0)} />
          </Field>
        </div>
        <label className="check">
          <input type="checkbox" checked={draft.feedPaused} onChange={(e) => set('feedPaused', e.target.checked)} /> Pause market data (quotes go stale,
          nothing fills)
        </label>
        <ErrorNote error={error} />
        <div className="form__actions">
          {saved && <span className="pos small">Saved</span>}
          <button
            className="btn btn--ghost"
            onClick={() => void save({ extraAckLatencyMs: 0, exchangeRejectPercent: 0, lostResponsePercent: 0, unavailablePercent: 0, feedPaused: false })}
          >
            All off
          </button>
          <button className="btn btn--primary" onClick={() => void save(draft)}>
            Apply
          </button>
        </div>
      </div>
    </Panel>
  );
}

function EndOfDay() {
  const overview = usePoll(() => admin.get<Overview>('/overview'), 10_000, []);
  const [date, setDate] = useState(istToday());
  const [report, setReport] = useState<DayCloseReport | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);

  async function close() {
    if (!window.confirm(`Close ${date} now? Working orders expire, intraday positions close, and the day is settled for every account.`)) return;
    setBusy(true);
    setError(null);
    try {
      setReport(await admin.post<DayCloseReport>('/end-of-day', { tradingDate: date }));
      overview.reload();
    } catch (err) {
      setError(err as ApiError);
    } finally {
      setBusy(false);
    }
  }

  const feed = overview.data?.feed;
  return (
    <div className="stack">
      <Panel title="End the trading day">
        <p className="small-note">
          The broker settles each day by itself at 23:58 IST. Here it can be done now, to see a whole day's cycle in minutes: orders expire,
          intraday positions close at the last price, expiries settle, futures are marked to market, delivery moves into holdings, and P&amp;L
          and charges reach the ledger.
        </p>
        <div className="inline-form">
          <input className="input input--sm" type="date" value={date} onChange={(e) => setDate(e.target.value || istToday())} />
          <button className="btn btn--primary" disabled={busy} onClick={() => void close()}>
            {busy ? 'Settling…' : 'Close the day now'}
          </button>
        </div>
        <ErrorNote error={error} />
        {report && (
          <div className="note">
            {report.tradingDate} closed: {report.ordersExpired} order(s) expired, {report.accountsSettled} account(s) settled.
          </div>
        )}
        <p className="small-note">Last closed: {overview.data?.broker.lastClosedDate ?? 'never'}</p>
      </Panel>
      <Panel title="Market data">
        {feed && (
          <Kv
            rows={[
              ['Live feed (Redis)', feed.redisConfigured ? `${num(feed.redisTicks)} ticks read` : 'Not configured'],
              ['Offline market', feed.simulatorRunning ? <Badge tone="live">Running</Badge> : 'Stopped'],
              ['Paused by chaos mode', feed.paused ? <Badge tone="warn">Yes</Badge> : 'No'],
              ['Quotes held', num(feed.quotes)],
              ['Last tick', ago(feed.latestTickAt)],
            ]}
          />
        )}
      </Panel>
    </div>
  );
}

export function Sandbox() {
  return (
    <Shell title="Sandbox">
      <div className="grid grid--2">
        <Simulator />
        <div className="stack">
          <ChaosPanel />
          <EndOfDay />
        </div>
      </div>
    </Shell>
  );
}
