import { useState } from 'react';
import { Shell } from '../components/Shell';
import { Badge, ErrorNote, Kv, Panel } from '../components/ui';
import { admin, ApiError } from '../lib/api';
import { clock, istTime, label, price } from '../lib/format';
import type { Calendar, Instrument, Profile, Quote } from '../lib/types';
import { usePoll } from '../lib/usePoll';

function ProfilePanel({ profile }: { profile: Profile }) {
  return (
    <Panel title={`Broker profile · ${profile.name}`}>
      <Kv
        rows={[
          ['Order operations', `${profile.rateLimits.orderOpsPerSecond}/s, place + modify + cancel`],
          [
            'Requests',
            `${profile.rateLimits.requestsPerSecond}/s · ${profile.rateLimits.requestsPerMinute}/min · ${profile.rateLimits.requestsPerDay.toLocaleString('en-IN')}/day`,
          ],
          ['Static IPs', `${profile.maxStaticIpsPerApp} per app · ${profile.staticIpChangesPerWeek} change a week`],
          ['Session', `Ends ${clock(profile.sessionExpiresAtIst)} IST; TOTP login each day`],
          ['Market orders', profile.allowMarketOrders ? 'Allowed' : 'Refused'],
          ['Commodity IOC', profile.allowIocInCommodities ? 'Allowed' : 'Refused'],
          ['Algo ID', profile.algoId],
        ]}
      />
    </Panel>
  );
}

function InstrumentLookup() {
  const [symbol, setSymbol] = useState('NSE:NIFTY26SEPFUT');
  const [instrument, setInstrument] = useState<Instrument | null>(null);
  const [quote, setQuote] = useState<Quote | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [last, setLast] = useState('');
  const [saved, setSaved] = useState(false);

  async function look(e?: React.FormEvent) {
    e?.preventDefault();
    setError(null);
    setSaved(false);
    const s = symbol.trim().toUpperCase();
    try {
      setInstrument(await admin.get<Instrument>(`/instruments?symbol=${encodeURIComponent(s)}`));
    } catch (err) {
      setInstrument(null);
      setError(err as ApiError);
    }
    try {
      setQuote(await admin.get<Quote>(`/quotes?symbol=${encodeURIComponent(s)}`));
    } catch {
      setQuote(null);
    }
  }

  async function setPrice(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      setQuote(await admin.put<Quote>('/quotes', { symbol: symbol.trim().toUpperCase(), lastPrice: Number(last) }));
      setSaved(true);
    } catch (err) {
      setError(err as ApiError);
    }
  }

  return (
    <Panel title="Instrument lookup">
      <form className="inline-form" onSubmit={look}>
        <input className="input mono" value={symbol} onChange={(e) => setSymbol(e.target.value)} />
        <button className="btn btn--primary">Look up</button>
      </form>
      <ErrorNote error={error} />
      {instrument && (
        <Kv
          rows={[
            ['Kind', `${label(instrument.kind)} · ${label(instrument.segment)} · ${instrument.exchange}`],
            ['Underlying', `${instrument.underlying} (${label(instrument.underlyingClass)})`],
            ['Lot size', instrument.lotSize || <Badge tone="neg">unknown: not tradable</Badge>],
            ['Tick size', instrument.tickSize],
            ['Freeze quantity', instrument.freezeQuantity ?? 'none'],
            ['Expiry', instrument.expiry ?? '—'],
            ['Strike', instrument.strike ? `${price(instrument.strike)} ${instrument.right ?? ''}` : '—'],
            ['Session', `${clock(instrument.session.opens)}–${clock(instrument.session.closes)} IST`],
            ['Exchange token', instrument.exchangeToken ?? '—'],
            ['Last price', quote ? `${price(quote.lastPrice)} at ${istTime(quote.at)}` : 'No quote yet'],
          ]}
        />
      )}
      {instrument && (
        <form className="inline-form" onSubmit={setPrice}>
          <input className="input mono" inputMode="decimal" placeholder="Last price" value={last} onChange={(e) => setLast(e.target.value)} />
          <button className="btn btn--ghost" disabled={!last}>
            Set quote
          </button>
          {saved && <span className="pos">Saved</span>}
        </form>
      )}
      <p className="small-note">Without the live Redis feed, set a price by hand: stop triggers and the cash price band are checked against it.</p>
    </Panel>
  );
}

const closureText: Record<string, string> = {
  FULL_DAY: 'Closed',
  MORNING_SESSION: 'Evening only',
  EVENING_SESSION: 'Morning only',
};

export function Rules() {
  const profiles = usePoll(() => admin.get<Profile[]>('/profiles'), 0, []);
  const calendar = usePoll(() => admin.get<Calendar>('/calendar'), 0, []);
  const [exchange, setExchange] = useState<'ALL' | 'NSE' | 'BSE' | 'MCX'>('ALL');

  const holidays = (calendar.data?.holidays ?? []).filter((h) => exchange === 'ALL' || h.exchange === exchange);

  return (
    <Shell title="Rules & calendar">
      <ErrorNote error={profiles.error ?? calendar.error} />
      <div className="grid grid--2">
        <div className="stack">
          {profiles.data?.map((p) => <ProfilePanel key={p.id} profile={p} />)}
          <InstrumentLookup />
        </div>
        <Panel
          title="Trading holidays"
          aside={
            <div className="segmented">
              {(['ALL', 'NSE', 'BSE', 'MCX'] as const).map((x) => (
                <button key={x} className={exchange === x ? 'segmented__on' : ''} onClick={() => setExchange(x)}>
                  {x === 'ALL' ? 'All' : x}
                </button>
              ))}
            </div>
          }
          flush
        >
          <table className="table table--dense">
            <thead>
              <tr>
                <th>Date</th>
                <th>Exchange</th>
                <th>Holiday</th>
                <th>Trading</th>
                <th>Circular</th>
              </tr>
            </thead>
            <tbody>
              {calendar.data?.specialSessions
                .filter((s) => exchange === 'ALL' || s.exchange === exchange)
                .map((s) => (
                  <tr key={`s-${s.exchange}-${s.date}`}>
                    <td className="mono">{s.date}</td>
                    <td>{s.exchange}</td>
                    <td>{s.name}</td>
                    <td>
                      <Badge tone="live">{`Special ${clock(s.opens)}–${clock(s.closes)}`}</Badge>
                    </td>
                    <td className="dim small">{s.source}</td>
                  </tr>
                ))}
              {holidays.map((h) => (
                <tr key={`${h.exchange}-${h.date}`}>
                  <td className="mono">{h.date}</td>
                  <td>{h.exchange}</td>
                  <td>{h.name}</td>
                  <td>
                    <Badge tone={h.closure === 'FULL_DAY' ? 'muted' : 'warn'}>{closureText[h.closure]}</Badge>
                  </td>
                  <td className="dim small">{h.source}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>
      </div>
    </Shell>
  );
}
