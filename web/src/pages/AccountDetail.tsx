import { useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { OrderDrawer, OrderTable } from '../components/Orders';
import { JournalList, LatencySummary, RequestTable } from '../components/Requests';
import { Shell } from '../components/Shell';
import { Empty, ErrorNote, Field, Kv, Panel, Secret, Stat, Tabs } from '../components/ui';
import { admin, ApiError } from '../lib/api';
import { clock, istDateTime, istToday, rupees } from '../lib/format';
import type { AccountView, Funds, JournalEvent, Order, RegisteredApp, RequestEntry } from '../lib/types';
import { usePoll } from '../lib/usePoll';

const tabs = [
  { id: 'orders', label: 'Orders' },
  { id: 'funds', label: 'Funds' },
  { id: 'apps', label: 'API apps' },
  { id: 'journal', label: 'Journal' },
  { id: 'requests', label: 'Requests' },
  { id: 'limits', label: 'Limits' },
] as const;
type TabId = (typeof tabs)[number]['id'];

function OrdersTab({ clientId }: { clientId: string }) {
  const [date, setDate] = useState(istToday());
  const [selected, setSelected] = useState<Order | null>(null);
  const orders = usePoll(() => admin.get<Order[]>(`/accounts/${clientId}/orders?date=${date}`), 2000, [clientId, date]);
  const detail = usePoll(
    () => (selected ? admin.get<Order>(`/accounts/${clientId}/orders/${selected.orderId}`) : Promise.resolve(null)),
    selected ? 2000 : 0,
    [clientId, selected?.orderId],
  );

  return (
    <Panel
      title="Order book"
      aside={<input className="input input--sm" type="date" value={date} onChange={(e) => setDate(e.target.value || istToday())} />}
      flush
    >
      <ErrorNote error={orders.error} />
      {orders.data && <OrderTable orders={orders.data} onOpen={setSelected} />}
      {selected && <OrderDrawer order={detail.data ?? selected} onClose={() => setSelected(null)} />}
    </Panel>
  );
}

function FundsTab({ clientId }: { clientId: string }) {
  const funds = usePoll(() => admin.get<Funds>(`/accounts/${clientId}/funds`), 3000, [clientId]);
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [error, setError] = useState<ApiError | null>(null);

  async function move(kind: 'funds' | 'withdrawals') {
    setError(null);
    try {
      await admin.post(`/accounts/${clientId}/${kind}`, { amount: Number(amount), reference: reference || null });
      setAmount('');
      setReference('');
      funds.reload();
    } catch (err) {
      setError(err as ApiError);
    }
  }

  const f = funds.data;
  return (
    <>
      {f && (
        <div className="stats stats--3">
          <Stat label="Pay-ins, net" value={rupees(f.netDeposits)} />
          <Stat label="Margin blocked" value={rupees(f.blockedMargin)} hint="Held by working orders" tone="warn" />
          <Stat label="Available" value={rupees(f.available)} tone="live" />
        </div>
      )}
      <div className="grid grid--2-1">
        <Panel title="Ledger" flush>
          {f && f.ledger.length === 0 && <Empty>No money has moved.</Empty>}
          {f && f.ledger.length > 0 && (
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
                {[...f.ledger].reverse().map((l) => (
                  <tr key={l.seq}>
                    <td className="mono dim">{istDateTime(l.at)}</td>
                    <td>{l.kind === 'PAY_IN' ? 'Pay-in' : 'Pay-out'}</td>
                    <td className="dim">{l.reference}</td>
                    <td className={l.amount >= 0 ? 'num mono pos' : 'num mono neg'}>{rupees(l.amount)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Panel>
        <Panel title="Move money">
          <div className="form">
            <Field label="Amount (₹)">
              <input className="input mono" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} placeholder="100000" />
            </Field>
            <Field label="Reference" hint="Optional: a UTR or a note.">
              <input className="input" value={reference} onChange={(e) => setReference(e.target.value)} />
            </Field>
            <ErrorNote error={error} />
            <div className="form__actions">
              <button className="btn btn--ghost" disabled={!amount} onClick={() => void move('withdrawals')}>
                Pay out
              </button>
              <button className="btn btn--primary" disabled={!amount} onClick={() => void move('funds')}>
                Pay in
              </button>
            </div>
          </div>
        </Panel>
      </div>
    </>
  );
}

function AppsTab({ clientId, account, reload }: { clientId: string; account: AccountView; reload: () => void }) {
  const [ips, setIps] = useState('');
  const [issued, setIssued] = useState<RegisteredApp | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  async function issue(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const staticIps = ips.split(/[\s,]+/).filter(Boolean);
      setIssued(await admin.post<RegisteredApp>(`/accounts/${clientId}/apps`, { staticIps }));
      setIps('');
      reload();
    } catch (err) {
      setError(err as ApiError);
    }
  }

  return (
    <div className="grid grid--2-1">
      <Panel title="API apps" flush>
        {account.apps.length === 0 ? (
          <Empty>No apps yet. A client needs one to log in through the API.</Empty>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>App ID</th>
                <th>Whitelisted IPs</th>
                <th className="num">IP changes this week</th>
                <th>Session</th>
              </tr>
            </thead>
            <tbody>
              {account.apps.map((app) => (
                <tr key={app.appId}>
                  <td className="mono">{app.appId}</td>
                  <td className="mono">{app.staticIps.join(', ') || <span className="dim">none: orders will be refused</span>}</td>
                  <td className="num mono">
                    {app.staticIpChangesThisWeek} / {account.profile.staticIpChangesPerWeek}
                  </td>
                  <td>{app.sessionExpiresAt ? <span className="pos">Logged in until {istDateTime(app.sessionExpiresAt)}</span> : <span className="dim">Not logged in</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Panel>
      <Panel title="Issue an app">
        {issued ? (
          <div className="form">
            <div className="note note--warn">Copy the secret now. The broker stores only its hash and cannot show it again.</div>
            <Secret label="App ID" value={issued.appId} />
            <Secret label="App secret" value={issued.appSecret} />
            <button className="btn btn--ghost" onClick={() => setIssued(null)}>
              Done
            </button>
          </div>
        ) : (
          <form className="form" onSubmit={issue}>
            <Field
              label="Static IP"
              hint={`Orders are accepted only from this address. ${account.profile.name} allows ${account.profile.maxStaticIpsPerApp} per app. For a browser on this machine use 127.0.0.1 or ::1.`}
            >
              <input className="input mono" value={ips} onChange={(e) => setIps(e.target.value)} placeholder="127.0.0.1" />
            </Field>
            <ErrorNote error={error} />
            <button className="btn btn--primary">Issue app</button>
          </form>
        )}
      </Panel>
    </div>
  );
}

function JournalTab({ clientId }: { clientId: string }) {
  const journal = usePoll(() => admin.get<JournalEvent[]>(`/accounts/${clientId}/journal?limit=1000`), 3000, [clientId]);
  return (
    <Panel title="Journal" aside={<span className="dim">Every event recorded for this account, newest first. Click one for its JSON.</span>} flush>
      <ErrorNote error={journal.error} />
      {journal.data && <JournalList events={journal.data} />}
    </Panel>
  );
}

function RequestsTab({ clientId }: { clientId: string }) {
  const requests = usePoll(() => admin.get<RequestEntry[]>(`/accounts/${clientId}/requests?limit=200`), 2000, [clientId]);
  return (
    <Panel title="API calls" aside={requests.data && <LatencySummary requests={requests.data} />} flush>
      <ErrorNote error={requests.error} />
      {requests.data && <RequestTable requests={requests.data} />}
    </Panel>
  );
}

function LimitsTab({ account }: { account: AccountView }) {
  const p = account.profile;
  return (
    <div className="grid grid--2">
      <Panel title={`Access and rate · ${p.name}`}>
        <Kv
          rows={[
            ['Order operations', `${p.rateLimits.orderOpsPerSecond} per second (place, modify and cancel together)`],
            ['All requests', `${p.rateLimits.requestsPerSecond}/s · ${p.rateLimits.requestsPerMinute}/min · ${p.rateLimits.requestsPerDay.toLocaleString('en-IN')}/day`],
            ['Day block', `After ${p.rateLimits.minuteBreachesAllowedPerDay} minutes at the per-minute limit`],
            ['Static IPs', `${p.maxStaticIpsPerApp} per app, ${p.staticIpChangesPerWeek} change per calendar week`],
            ['Session ends', `${clock(p.sessionExpiresAtIst)} IST, then a fresh TOTP login`],
            ['Algo ID', <span className="mono">{p.algoId}</span>],
          ]}
        />
      </Panel>
      <Panel title="Orders and margin">
        <Kv
          rows={[
            ['Market orders', p.allowMarketOrders ? 'Allowed' : 'Refused (exchange rule for algo orders)'],
            ['IOC in commodities', p.allowIocInCommodities ? 'Allowed' : 'Refused'],
            ['Modifications per order', p.maxModificationsPerOrder ?? 'No limit published'],
            ['New MIS orders until', Object.entries(p.intradayCutoffIst).map(([x, t]) => `${x} ${clock(t)}`).join(' · ')],
            ['Cash price band', `${p.cashPriceBandPercent}% from the previous close`],
            ['Intraday equity margin', `${p.margins.equityIntradayPercent}% of value`],
            [
              'Derivative margin',
              `${p.margins.indexDerivativePercent}% index · ${p.margins.stockDerivativePercent}% stock · ${p.margins.commodityDerivativePercent}% commodity`,
            ],
          ]}
        />
        <p className="small-note">
          Sources and assumptions for each value: <code>docs/rules.md</code>.
        </p>
      </Panel>
    </div>
  );
}

export function AccountDetail() {
  const { clientId = '' } = useParams();
  const [params, setParams] = useSearchParams();
  const tab = (tabs.find((t) => t.id === params.get('tab'))?.id ?? 'orders') as TabId;
  const account = usePoll(() => admin.get<AccountView>(`/accounts/${clientId}`), 5000, [clientId]);
  const a = account.data;

  return (
    <Shell
      title={
        <>
          <Link to="/accounts" className="crumb">
            Accounts
          </Link>{' '}
          / <span className="mono">{clientId}</span> {a && <span className="dim">· {a.name}</span>}
        </>
      }
    >
      <ErrorNote error={account.error} />
      <Tabs tabs={tabs} value={tab} onChange={(id) => setParams({ tab: id })} />
      {tab === 'orders' && <OrdersTab clientId={clientId} />}
      {tab === 'funds' && <FundsTab clientId={clientId} />}
      {tab === 'apps' && a && <AppsTab clientId={clientId} account={a} reload={account.reload} />}
      {tab === 'journal' && <JournalTab clientId={clientId} />}
      {tab === 'requests' && <RequestsTab clientId={clientId} />}
      {tab === 'limits' && a && <LimitsTab account={a} />}
    </Shell>
  );
}
