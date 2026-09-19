import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { admin, adminKey } from '../lib/api';
import { clock, istTime } from '../lib/format';
import type { Overview } from '../lib/types';
import { usePoll } from '../lib/usePoll';
import { Logo } from './ui';

const nav = [
  { to: '/', label: 'Overview', end: true },
  { to: '/accounts', label: 'Accounts' },
  { to: '/activity', label: 'Activity' },
  { to: '/terminal', label: 'Trader terminal' },
  { to: '/sandbox', label: 'Sandbox' },
  { to: '/rules', label: 'Rules & calendar' },
];

function IstClock() {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 1000);
    return () => window.clearInterval(timer);
  }, []);
  return (
    <span className="clock" title="India Standard Time">
      {istTime(now)} <span className="clock__zone">IST</span>
    </span>
  );
}

function MarketChips() {
  const { data } = usePoll(() => admin.get<Overview>('/overview'), 30_000, []);
  if (!data) return null;
  return (
    <div className="chips">
      {data.alwaysOpen && (
        <span className="chip chip--warn" title="Exchange:AlwaysOpen is on: trading hours are ignored">
          Hours ignored
        </span>
      )}
      {data.exchanges.map((x) => (
        <span
          key={x.exchange}
          className={x.open ? 'chip chip--open' : 'chip'}
          title={x.holiday ? `Holiday: ${x.holiday}` : x.today ? `${clock(x.today.opens)}–${clock(x.today.closes)} IST` : 'Not trading today'}
        >
          <span className="chip__dot" />
          {x.exchange}
          <span className="chip__state">{x.open ? 'open' : x.holiday ? 'holiday' : 'closed'}</span>
        </span>
      ))}
    </div>
  );
}

export function Shell({ title, actions, children }: { title: ReactNode; actions?: ReactNode; children: ReactNode }) {
  const navigate = useNavigate();
  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <Logo size={24} />
          <div>
            <div className="brand__name">OpenFNO Broker</div>
            <div className="brand__sub">Back office</div>
          </div>
        </div>
        <nav className="nav">
          {nav.map((item) => (
            <NavLink key={item.to} to={item.to} end={item.end} className={({ isActive }) => (isActive ? 'nav__link nav__link--on' : 'nav__link')}>
              {item.label}
            </NavLink>
          ))}
        </nav>
        <div className="sidebar__foot">
          <p className="sidebar__note">A simulated broker. No money moves and no order reaches an exchange.</p>
          <button
            className="btn btn--ghost btn--sm"
            onClick={() => {
              adminKey.set(null);
              navigate('/signin');
            }}
          >
            Sign out
          </button>
        </div>
      </aside>
      <div className="main">
        <header className="topbar">
          <h1 className="topbar__title">{title}</h1>
          <div className="topbar__right">
            {actions}
            <MarketChips />
            <IstClock />
          </div>
        </header>
        <main className="content">{children}</main>
      </div>
    </div>
  );
}
