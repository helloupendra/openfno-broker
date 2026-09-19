import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Shell } from '../components/Shell';
import { Pnl } from '../components/Trading';
import { Badge, Empty, ErrorNote, Field, Modal, Panel, QrCode, Secret } from '../components/ui';
import { admin, ApiError } from '../lib/api';
import { rupees } from '../lib/format';
import type { AccountSummary, OpenedAccount } from '../lib/types';
import { usePoll } from '../lib/usePoll';

function OpenAccount({ onClose, onOpened }: { onClose: () => void; onOpened: () => void }) {
  const navigate = useNavigate();
  const [name, setName] = useState('');
  const [error, setError] = useState<ApiError | null>(null);
  const [opened, setOpened] = useState<OpenedAccount | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      setOpened(await admin.post<OpenedAccount>('/accounts', { name, profile: 'fyers' }));
      onOpened();
    } catch (err) {
      setError(err as ApiError);
    }
  }

  return (
    <Modal title={opened ? `Account ${opened.clientId} is open` : 'Open an account'} onClose={onClose}>
      {!opened ? (
        <form className="form" onSubmit={submit}>
          <Field label="Name">
            <input className="input" autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="Trader's name" />
          </Field>
          <Field label="Broker profile" hint="The limits the account trades under. FYERS is the only profile so far.">
            <input className="input" value="FYERS API v3" disabled />
          </Field>
          <ErrorNote error={error} />
          <div className="form__actions">
            <button type="button" className="btn btn--ghost" onClick={onClose}>
              Cancel
            </button>
            <button className="btn btn--primary" disabled={!name.trim()}>
              Open account
            </button>
          </div>
        </form>
      ) : (
        <div className="onboard">
          <div className="note note--warn">
            Scan this into an authenticator app now. The broker keeps the secret encrypted and never shows it again.
          </div>
          <div className="onboard__qr">
            <QrCode text={opened.totpUri} />
            <div className="onboard__secrets">
              <Secret label="Client ID" value={opened.clientId} />
              <Secret label="TOTP secret" value={opened.totpSecret} />
            </div>
          </div>
          <div className="form__actions">
            <button className="btn btn--primary" onClick={() => navigate(`/accounts/${opened.clientId}?tab=apps`)}>
              Next: fund it and issue an API app
            </button>
          </div>
        </div>
      )}
    </Modal>
  );
}

export function Accounts() {
  const accounts = usePoll(() => admin.get<AccountSummary[]>('/accounts'), 5000, []);
  const [opening, setOpening] = useState(false);

  return (
    <Shell
      title="Accounts"
      actions={
        <button className="btn btn--primary btn--sm" onClick={() => setOpening(true)}>
          Open account
        </button>
      }
    >
      <ErrorNote error={accounts.error} />
      <Panel flush>
        {accounts.data && accounts.data.length === 0 && (
          <Empty>
            No accounts yet.{' '}
            <button className="link" onClick={() => setOpening(true)}>
              Open the first one
            </button>
            .
          </Empty>
        )}
        {accounts.data && accounts.data.length > 0 && (
          <div className="table-wrap">
            <table className="table table--hover">
              <thead>
                <tr>
                  <th>Client ID</th>
                  <th>Name</th>
                  <th>Profile</th>
                  <th className="num">Pay-ins, net</th>
                  <th className="num">Available</th>
                  <th className="num">Realised today</th>
                  <th className="num">Positions</th>
                  <th className="num">Working orders</th>
                  <th className="num">Apps</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {accounts.data.map((a) => (
                  <tr key={a.clientId}>
                    <td className="mono">
                      <Link to={`/accounts/${a.clientId}`}>{a.clientId}</Link>
                    </td>
                    <td>{a.name}</td>
                    <td className="dim">{a.profileId.toUpperCase()}</td>
                    <td className="num mono">{rupees(a.netDeposits)}</td>
                    <td className="num mono">{rupees(a.available)}</td>
                    <td className="num">
                      <Pnl value={a.realisedToday} />
                    </td>
                    <td className="num mono">{a.openPositions}</td>
                    <td className="num mono">{a.liveOrders}</td>
                    <td className="num mono">{a.apps}</td>
                    <td>{a.killSwitch && <Badge tone="neg">Kill switch</Badge>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      {opening && <OpenAccount onClose={() => setOpening(false)} onOpened={accounts.reload} />}
    </Shell>
  );
}
