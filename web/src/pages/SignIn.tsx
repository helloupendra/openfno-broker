import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { admin, adminKey, ApiError } from '../lib/api';
import type { Overview } from '../lib/types';
import { Logo } from '../components/ui';

export function SignIn() {
  const navigate = useNavigate();
  const [key, setKey] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await admin.get<Overview>('/overview', key.trim());
      adminKey.set(key.trim());
      navigate('/');
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="signin">
      <form className="signin__card" onSubmit={submit}>
        <div className="signin__brand">
          <Logo size={30} />
          <div>
            <div className="brand__name">OpenFNO Broker</div>
            <div className="brand__sub">Back office</div>
          </div>
        </div>
        <p className="signin__lede">
          A simulated Indian broker. It checks every order the way a broker and an exchange do, and records every step.
        </p>
        <label className="field">
          <span className="field__label">Admin key</span>
          <input
            className="input mono"
            type="password"
            autoFocus
            autoComplete="current-password"
            value={key}
            onChange={(e) => setKey(e.target.value)}
            placeholder="Broker:AdminKey"
          />
        </label>
        {error && <div className="note note--neg">{error}</div>}
        <button className="btn btn--primary btn--block" disabled={busy || key.trim().length === 0}>
          {busy ? 'Checking…' : 'Open the back office'}
        </button>
        <p className="signin__foot">
          The key is kept in this tab only. In development it is <code>dev-admin-key</code>.
        </p>
      </form>
    </div>
  );
}
