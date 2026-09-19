import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { OrderDrawer, OrderTable } from '../components/Orders';
import { Shell } from '../components/Shell';
import { Pnl, PositionsTable, TradesTable } from '../components/Trading';
import { Badge, ErrorNote, Field, Kv, Live, Modal, Panel, Side, StatusBadge, Tabs } from '../components/ui';
import { ApiError, trader, traderSession, type TraderSession } from '../lib/api';
import { clock, istDateTime, istTime, ms, price, rupees } from '../lib/format';
import type { Funds, Instrument, KillSwitch, Order, OrderType, Position, Product, Quote, Trade, Validity } from '../lib/types';
import { usePoll } from '../lib/usePoll';
import { useStream } from '../lib/useStream';

function Login({ onLogin }: { onLogin: (session: TraderSession) => void }) {
  const [form, setForm] = useState({ clientId: '', appId: '', appSecret: '', totp: '' });
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: e.target.value });

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const grant = await trader.login({ ...form, clientId: form.clientId.trim(), appId: form.appId.trim(), totp: form.totp.trim() });
      const session = { token: grant.accessToken, clientId: grant.clientId, appId: grant.appId, expiresAt: grant.expiresAt };
      traderSession.set(session);
      onLogin(session);
    } catch (err) {
      setError(err as ApiError);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Daily login">
      <form className="form" onSubmit={submit}>
        <p className="small-note">
          Log in the way a trading program does: app credentials plus a fresh TOTP code. The session lasts until 06:00 IST. Open an account and issue an
          app in <Link to="/accounts">Accounts</Link> first.
        </p>
        <div className="form__row">
          <Field label="Client ID">
            <input className="input mono" value={form.clientId} onChange={set('clientId')} placeholder="OFB00001" />
          </Field>
          <Field label="TOTP code">
            <input className="input mono" inputMode="numeric" maxLength={6} value={form.totp} onChange={set('totp')} placeholder="123456" />
          </Field>
        </div>
        <Field label="App ID">
          <input className="input mono" value={form.appId} onChange={set('appId')} placeholder="APP-…" />
        </Field>
        <Field label="App secret">
          <input className="input mono" type="password" value={form.appSecret} onChange={set('appSecret')} />
        </Field>
        <ErrorNote error={error} />
        <button className="btn btn--primary" disabled={busy}>
          {busy ? 'Logging in…' : 'Log in'}
        </button>
      </form>
    </Panel>
  );
}

interface Draft {
  symbol: string;
  side: 'BUY' | 'SELL';
  quantity: string;
  type: OrderType;
  product: Product;
  validity: Validity;
  limitPrice: string;
  triggerPrice: string;
  tag: string;
}

type Outcome = { ok: true; order: Order; elapsed: number } | { ok: false; error: ApiError; elapsed: number };

function useInstrument(token: string, symbol: string) {
  const [instrument, setInstrument] = useState<Instrument | null>(null);
  const [quote, setQuote] = useState<Quote | null>(null);
  useEffect(() => {
    const s = symbol.trim().toUpperCase();
    if (!s.includes(':')) {
      setInstrument(null);
      setQuote(null);
      return;
    }
    const timer = window.setTimeout(() => {
      trader.get<Instrument>(token, `/instruments?symbol=${encodeURIComponent(s)}`).then(setInstrument, () => setInstrument(null));
      trader.get<Quote>(token, `/quotes?symbol=${encodeURIComponent(s)}`).then(setQuote, () => setQuote(null));
    }, 350);
    return () => window.clearTimeout(timer);
  }, [token, symbol]);
  return { instrument, quote };
}

function OrderPad({ session, onPlaced }: { session: TraderSession; onPlaced: () => void }) {
  const [draft, setDraft] = useState<Draft>({
    symbol: 'NSE:SBIN-EQ',
    side: 'BUY',
    quantity: '1',
    type: 'LIMIT',
    product: 'MIS',
    validity: 'DAY',
    limitPrice: '',
    triggerPrice: '',
    tag: '',
  });
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [busy, setBusy] = useState(false);
  const { instrument, quote } = useInstrument(session.token, draft.symbol);
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft({ ...draft, [key]: value });

  const isStop = draft.type === 'STOP_LIMIT' || draft.type === 'STOP_MARKET';
  const isMarket = draft.type === 'MARKET' || draft.type === 'STOP_MARKET';
  const lot = instrument?.lotSize ?? 0;
  const quantity = Number(draft.quantity) || 0;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    const started = performance.now();
    try {
      const order = await trader.post<Order>(session.token, '/orders', {
        symbol: draft.symbol.trim().toUpperCase(),
        side: draft.side,
        quantity,
        type: draft.type,
        product: draft.product,
        validity: draft.validity,
        limitPrice: isMarket || !draft.limitPrice ? null : Number(draft.limitPrice),
        triggerPrice: isStop && draft.triggerPrice ? Number(draft.triggerPrice) : null,
        tag: draft.tag.trim() || null,
      });
      setOutcome({ ok: true, order, elapsed: performance.now() - started });
      onPlaced();
    } catch (err) {
      setOutcome({ ok: false, error: err as ApiError, elapsed: performance.now() - started });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Order pad">
      <form className="form" onSubmit={submit}>
        <Field label="Instrument" hint={instrument ? undefined : 'An exchange-prefixed symbol: NSE:SBIN-EQ, NSE:NIFTY26SEPFUT, NSE:NIFTY26SEP25000CE'}>
          <input className="input mono" value={draft.symbol} onChange={(e) => set('symbol', e.target.value)} />
        </Field>
        {instrument && (
          <div className="instrument">
            <span>
              Lot <b className="mono">{instrument.lotSize || '?'}</b>
            </span>
            <span>
              Tick <b className="mono">{instrument.tickSize}</b>
            </span>
            <span>
              Freeze <b className="mono">{instrument.freezeQuantity ?? '—'}</b>
            </span>
            <span>
              Session <b className="mono">{`${clock(instrument.session.opens)}–${clock(instrument.session.closes)}`}</b>
            </span>
            {instrument.expiry && (
              <span>
                Expiry <b className="mono">{instrument.expiry}</b>
              </span>
            )}
            <span>
              LTP <b className="mono">{quote ? price(quote.lastPrice) : '—'}</b>
            </span>
          </div>
        )}
        <div className="segmented segmented--side">
          {(['BUY', 'SELL'] as const).map((s) => (
            <button type="button" key={s} className={draft.side === s ? `segmented__on segmented__on--${s.toLowerCase()}` : ''} onClick={() => set('side', s)}>
              {s}
            </button>
          ))}
        </div>
        <div className="form__row">
          <Field label="Quantity (units)" hint={lot > 1 ? `${(quantity / lot).toFixed(quantity % lot ? 2 : 0)} lots of ${lot}` : undefined}>
            <div className="stepper">
              <input className="input mono" inputMode="numeric" value={draft.quantity} onChange={(e) => set('quantity', e.target.value)} />
              {lot > 1 && (
                <>
                  <button type="button" className="btn btn--ghost btn--sm" onClick={() => set('quantity', String(Math.max(0, quantity - lot)))}>
                    −lot
                  </button>
                  <button type="button" className="btn btn--ghost btn--sm" onClick={() => set('quantity', String(quantity + lot))}>
                    +lot
                  </button>
                </>
              )}
            </div>
          </Field>
          <Field label="Product">
            <select className="input" value={draft.product} onChange={(e) => set('product', e.target.value as Product)}>
              <option value="MIS">MIS · intraday</option>
              <option value="CNC">CNC · delivery</option>
              <option value="NRML">NRML · carry forward</option>
            </select>
          </Field>
        </div>
        <div className="form__row">
          <Field label="Type">
            <select className="input" value={draft.type} onChange={(e) => set('type', e.target.value as OrderType)}>
              <option value="LIMIT">LIMIT</option>
              <option value="STOP_LIMIT">STOP_LIMIT (SL)</option>
              <option value="MARKET">MARKET (refused for algo orders)</option>
              <option value="STOP_MARKET">STOP_MARKET (SL-M, refused)</option>
            </select>
          </Field>
          <Field label="Validity">
            <select className="input" value={draft.validity} onChange={(e) => set('validity', e.target.value as Validity)}>
              <option value="DAY">DAY</option>
              <option value="IOC">IOC</option>
            </select>
          </Field>
        </div>
        <div className="form__row">
          <Field label="Limit price">
            <input className="input mono" inputMode="decimal" disabled={isMarket} value={draft.limitPrice} onChange={(e) => set('limitPrice', e.target.value)} />
          </Field>
          <Field label="Trigger price">
            <input className="input mono" inputMode="decimal" disabled={!isStop} value={draft.triggerPrice} onChange={(e) => set('triggerPrice', e.target.value)} />
          </Field>
        </div>
        <Field label="Tag" hint="Optional, up to 20 letters, digits, - and _">
          <input className="input mono" value={draft.tag} onChange={(e) => set('tag', e.target.value)} />
        </Field>
        <button className={draft.side === 'BUY' ? 'btn btn--buy' : 'btn btn--sell'} disabled={busy}>
          {busy ? 'Sending…' : `${draft.side === 'BUY' ? 'Buy' : 'Sell'} ${quantity || ''} ${draft.symbol.trim().toUpperCase()}`}
        </button>
      </form>
      {outcome && (
        <div className={outcome.ok && outcome.order.status !== 'REJECTED' ? 'outcome outcome--ok' : 'outcome outcome--neg'}>
          <div className="outcome__head">
            {outcome.ok ? (
              <>
                <span>
                  Order <span className="mono">{outcome.order.orderId}</span>
                </span>
                <StatusBadge status={outcome.order.status} />
              </>
            ) : (
              <>
                <Badge tone="neg">HTTP {outcome.error.status || '—'}</Badge>
                <code>{outcome.error.code}</code>
              </>
            )}
            <span className="outcome__time mono">{ms(outcome.elapsed)} round trip</span>
          </div>
          <p>
            {outcome.ok
              ? outcome.order.message ?? `Margin blocked ${rupees(outcome.order.blockedMargin)}. The exchange acknowledges it in a few milliseconds.`
              : outcome.error.message}
          </p>
          {!outcome.ok && outcome.error.code !== 'STATIC_IP_MISMATCH' && (
            <p className="small-note">No order was created: this is an input error, not a risk rejection.</p>
          )}
        </div>
      )}
    </Panel>
  );
}

function ModifyOrder({ order, token, onClose, onDone }: { order: Order; token: string; onClose: () => void; onDone: () => void }) {
  const [quantity, setQuantity] = useState(String(order.quantity));
  const [limitPrice, setLimitPrice] = useState(String(order.limitPrice ?? ''));
  const [triggerPrice, setTriggerPrice] = useState(String(order.triggerPrice ?? ''));
  const [error, setError] = useState<ApiError | null>(null);
  const isStop = order.type === 'STOP_LIMIT' || order.type === 'STOP_MARKET';

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    try {
      await trader.patch(token, `/orders/${order.orderId}`, {
        quantity: Number(quantity),
        limitPrice: limitPrice ? Number(limitPrice) : null,
        triggerPrice: isStop && triggerPrice ? Number(triggerPrice) : null,
      });
      onDone();
      onClose();
    } catch (err) {
      setError(err as ApiError);
    }
  }

  return (
    <Modal title={`Modify ${order.orderId}`} onClose={onClose}>
      <form className="form" onSubmit={submit}>
        <div className="drawer__summary">
          <Side side={order.side} /> <span className="mono">{order.symbol}</span> <StatusBadge status={order.status} />
        </div>
        <div className="form__row">
          <Field label="Quantity">
            <input className="input mono" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
          </Field>
          <Field label="Limit price">
            <input className="input mono" value={limitPrice} onChange={(e) => setLimitPrice(e.target.value)} />
          </Field>
          {isStop && (
            <Field label="Trigger price">
              <input className="input mono" value={triggerPrice} onChange={(e) => setTriggerPrice(e.target.value)} />
            </Field>
          )}
        </div>
        <ErrorNote error={error} />
        <div className="form__actions">
          <button type="button" className="btn btn--ghost" onClick={onClose}>
            Close
          </button>
          <button className="btn btn--primary">Send modification</button>
        </div>
      </form>
    </Modal>
  );
}

type DeskTab = 'orders' | 'positions' | 'trades';

function KillSwitchButton({ session, onChanged }: { session: TraderSession; onChanged: () => void }) {
  const [open, setOpen] = useState(false);
  const [squareOff, setSquareOff] = useState(true);
  const [error, setError] = useState<ApiError | null>(null);
  const state = usePoll(() => trader.get<KillSwitch>(session.token, '/kill-switch'), 15_000, [session.token]);

  async function activate() {
    setError(null);
    try {
      await trader.post(session.token, '/kill-switch', { active: true, squareOff });
      setOpen(false);
      state.reload();
      onChanged();
    } catch (err) {
      setError(err as ApiError);
    }
  }

  if (state.data?.active)
    return <Badge tone="neg">Kill switch on until {istDateTime(state.data.until)}</Badge>;

  return (
    <>
      <button className="btn btn--danger btn--sm" onClick={() => setOpen(true)}>
        Kill switch
      </button>
      {open && (
        <Modal title="Turn on the kill switch?" onClose={() => setOpen(false)}>
          <div className="form">
            <p className="small-note">
              Every working order is cancelled and no new order is accepted until the next trading day. Only the back office can lift it
              sooner.
            </p>
            <label className="check">
              <input type="checkbox" checked={squareOff} onChange={(e) => setSquareOff(e.target.checked)} /> Also close every open position at the
              market
            </label>
            <ErrorNote error={error} />
            <div className="form__actions">
              <button className="btn btn--ghost" onClick={() => setOpen(false)}>
                Keep trading
              </button>
              <button className="btn btn--danger" onClick={() => void activate()}>
                Turn it on
              </button>
            </div>
          </div>
        </Modal>
      )}
    </>
  );
}

function Desk({ session }: { session: TraderSession }) {
  const [tab, setTab] = useState<DeskTab>('orders');
  const orders = usePoll(() => trader.get<Order[]>(session.token, '/orders'), 10_000, [session.token]);
  const positions = usePoll(() => trader.get<Position[]>(session.token, '/positions'), 5_000, [session.token]);
  const trades = usePoll(() => trader.get<Trade[]>(session.token, '/trades'), 10_000, [session.token]);
  const funds = usePoll(() => trader.get<Funds>(session.token, '/funds'), 5_000, [session.token]);
  const [selected, setSelected] = useState<Order | null>(null);
  const [modifying, setModifying] = useState<Order | null>(null);
  const [actionError, setActionError] = useState<ApiError | null>(null);
  const [lastEvent, setLastEvent] = useState<string | null>(null);
  const detail = usePoll(
    () => (selected ? trader.get<Order>(session.token, `/orders/${selected.orderId}`) : Promise.resolve(null)),
    selected ? 3000 : 0,
    [selected?.orderId],
  );

  const refresh = useDebounced(() => {
    orders.reload();
    positions.reload();
    trades.reload();
    funds.reload();
  }, 150);
  const live = useStream(`/api/v1/stream?access_token=${encodeURIComponent(session.token)}`, (e) => {
    if (e.event === 'stream.connected') return;
    setLastEvent(`${e.event} · ${istTime(String(e.at), true)}`);
    refresh();
  });

  async function cancel(order: Order) {
    setActionError(null);
    try {
      await trader.delete(session.token, `/orders/${order.orderId}`);
      refresh();
    } catch (err) {
      setActionError(err as ApiError);
    }
  }

  /** Closes a position with a limit order at the other side of the book, so it crosses at once. */
  async function exit(position: Position) {
    setActionError(null);
    try {
      const quote = await trader.get<Quote>(session.token, `/quotes?symbol=${encodeURIComponent(position.symbol)}`);
      const side = position.quantity > 0 ? 'SELL' : 'BUY';
      const limitPrice = side === 'SELL' ? quote.bid ?? quote.lastPrice : quote.ask ?? quote.lastPrice;
      await trader.post(session.token, '/orders', {
        symbol: position.symbol,
        side,
        quantity: Math.abs(position.quantity),
        type: 'LIMIT',
        product: position.product,
        validity: 'DAY',
        limitPrice,
        triggerPrice: null,
        tag: 'exit',
      });
      refresh();
    } catch (err) {
      setActionError(err as ApiError);
    }
  }

  const working = (o: Order) => o.status === 'OPEN' || o.status === 'TRIGGER_PENDING' || o.status === 'PARTIALLY_FILLED' || o.status === 'TRANSIT';
  const f = funds.data;

  return (
    <div className="stack">
      <div className="session-bar">
        <span>
          <span className="dim">Client</span> <b className="mono">{session.clientId}</b>
        </span>
        <span>
          <span className="dim">Session until</span> <b>{istDateTime(session.expiresAt)}</b>
        </span>
        {f && (
          <>
            <span>
              <span className="dim">Available</span> <b className="mono">{rupees(f.available)}</b>
            </span>
            <span>
              <span className="dim">Today after charges</span> <Pnl value={f.realisedToday - f.chargesToday} strong />
            </span>
          </>
        )}
        <span className="session-bar__live" title={lastEvent ?? 'Waiting for events'}>
          <Live on={live} /> {live ? 'Live' : 'Reconnecting…'}
        </span>
        <KillSwitchButton session={session} onChanged={refresh} />
      </div>
      <Panel
        title={<Tabs tabs={[
          { id: 'orders', label: 'Orders', count: orders.data?.length },
          { id: 'positions', label: 'Positions', count: positions.data?.filter((p) => p.quantity !== 0).length },
          { id: 'trades', label: 'Trades', count: trades.data?.length },
        ] as const} value={tab} onChange={setTab} />}
        flush
      >
        <ErrorNote error={actionError ?? orders.error} />
        {tab === 'orders' && orders.data && (
          <OrderTable
            orders={orders.data}
            onOpen={setSelected}
            compact
            empty="No orders yet today. Place one with the pad."
            actions={(o) =>
              working(o) && o.appId !== 'RMS' ? (
                <div className="row-actions">
                  <button className="btn btn--ghost btn--sm" onClick={() => setModifying(o)}>
                    Modify
                  </button>
                  <button className="btn btn--ghost btn--sm" onClick={() => void cancel(o)}>
                    Cancel
                  </button>
                </div>
              ) : null
            }
          />
        )}
        {tab === 'positions' && positions.data && (
          <PositionsTable
            positions={positions.data}
            compact
            actions={(p) =>
              p.quantity !== 0 ? (
                <button className="btn btn--ghost btn--sm" onClick={() => void exit(p)}>
                  Exit
                </button>
              ) : null
            }
          />
        )}
        {tab === 'trades' && trades.data && <TradesTable trades={trades.data} />}
      </Panel>
      {selected && <OrderDrawer order={detail.data ?? selected} onClose={() => setSelected(null)} />}
      {modifying && <ModifyOrder order={modifying} token={session.token} onClose={() => setModifying(null)} onDone={refresh} />}
    </div>
  );
}

function useDebounced(fn: () => void, ms: number): () => void {
  const timer = useRef<number | undefined>(undefined);
  const latest = useRef(fn);
  latest.current = fn;
  return useCallback(() => {
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => latest.current(), ms);
  }, [ms]);
}

export function Terminal() {
  const [session, setSession] = useState<TraderSession | null>(() => traderSession.get());
  const [ip, setIp] = useState<string | null>(null);
  const [bookKey, setBookKey] = useState(0);

  useEffect(() => {
    trader.whoami().then((r) => setIp(r.ip), () => setIp(null));
  }, []);

  return (
    <Shell
      title="Trader terminal"
      actions={
        session && (
          <button
            className="btn btn--ghost btn--sm"
            onClick={() => {
              void trader.logout(session.token).catch(() => undefined);
              traderSession.set(null);
              setSession(null);
            }}
          >
            Log out {session.clientId}
          </button>
        )
      }
    >
      <div className="note">
        The broker sees this browser as <code>{ip ?? 'unknown'}</code>. Orders are accepted only if that address is the app's static IP; reads work
        from anywhere.
      </div>
      {!session ? (
        <div className="grid grid--2">
          <Login onLogin={setSession} />
          <Panel title="What this terminal is for">
            <Kv
              rows={[
                ['Try the rules', 'Send orders that break them (a market order, 70 NIFTY, a price off the tick) and read the refusal'],
                ['Watch the lifecycle', 'Transit → open → cancelled or expired, each step with its time'],
                ['See the latency', 'Round trip here; the broker-side split in Accounts → Requests'],
                ['Same path as a program', 'This page calls the public API with a bearer token, like the engine will'],
              ]}
            />
          </Panel>
        </div>
      ) : (
        <div className="grid grid--pad">
          <OrderPad session={session} onPlaced={() => setBookKey((k) => k + 1)} />
          <Desk key={bookKey} session={session} />
        </div>
      )}
    </Shell>
  );
}
