import { useEffect, useState, type ReactNode } from 'react';
import QRCode from 'qrcode';
import type { ApiError } from '../lib/api';
import { label } from '../lib/format';
import type { OrderStatus } from '../lib/types';

export function Logo({ size = 22 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" className="logo">
      <g fill="currentColor">
        <rect x="4.9" y="8.4" width="1.2" height="9.4" rx=".6" opacity=".5" />
        <rect x="3.6" y="10.6" width="3.8" height="5.2" rx="1.2" />
        <rect x="11.4" y="4.6" width="1.2" height="12.6" rx=".6" opacity=".5" />
        <rect x="10.1" y="6.6" width="3.8" height="8.2" rx="1.2" />
        <rect x="17.9" y="6.6" width="1.2" height="10.6" rx=".6" opacity=".5" />
        <rect x="16.6" y="8.8" width="3.8" height="6.2" rx="1.2" />
      </g>
    </svg>
  );
}

const statusTone: Record<OrderStatus, string> = {
  TRANSIT: 'brand',
  OPEN: 'live',
  TRIGGER_PENDING: 'warn',
  PARTIALLY_FILLED: 'live',
  FILLED: 'pos',
  CANCELLED: 'muted',
  REJECTED: 'neg',
  EXPIRED: 'muted',
};

export function StatusBadge({ status }: { status: OrderStatus }) {
  return <span className={`badge badge--${statusTone[status]}`}>{label(status)}</span>;
}

export function Badge({ tone = 'muted', children }: { tone?: string; children: ReactNode }) {
  return <span className={`badge badge--${tone}`}>{children}</span>;
}

/** An HTTP status, coloured by class. */
export function HttpStatus({ status }: { status: number }) {
  const tone = status < 300 ? 'pos' : status === 429 ? 'warn' : 'neg';
  return <span className={`http http--${tone}`}>{status}</span>;
}

export function Side({ side }: { side: 'BUY' | 'SELL' }) {
  return <span className={side === 'BUY' ? 'side side--buy' : 'side side--sell'}>{side}</span>;
}

export function Panel({
  title,
  aside,
  children,
  flush,
}: {
  title?: ReactNode;
  aside?: ReactNode;
  children: ReactNode;
  flush?: boolean;
}) {
  return (
    <section className="panel">
      {(title || aside) && (
        <header className="panel__head">
          <h2>{title}</h2>
          {aside && <div className="panel__aside">{aside}</div>}
        </header>
      )}
      <div className={flush ? 'panel__body panel__body--flush' : 'panel__body'}>{children}</div>
    </section>
  );
}

export function Stat({ label, value, hint, tone }: { label: string; value: ReactNode; hint?: ReactNode; tone?: string }) {
  return (
    <div className={tone ? `stat stat--${tone}` : 'stat'}>
      <div className="stat__label">{label}</div>
      <div className="stat__value">{value}</div>
      {hint && <div className="stat__hint">{hint}</div>}
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

export function ErrorNote({ error }: { error: ApiError | null | undefined }) {
  if (!error) return null;
  return (
    <div className="note note--neg" role="alert">
      <code>{error.code}</code> {error.message}
    </div>
  );
}

export function Live({ on = true }: { on?: boolean }) {
  return <span className={on ? 'live-dot' : 'live-dot live-dot--off'} aria-hidden="true" />;
}

export function Tabs<T extends string>({
  tabs,
  value,
  onChange,
}: {
  tabs: readonly { id: T; label: string; count?: number }[];
  value: T;
  onChange: (id: T) => void;
}) {
  return (
    <div className="tabs" role="tablist">
      {tabs.map((tab) => (
        <button
          key={tab.id}
          role="tab"
          aria-selected={tab.id === value}
          className={tab.id === value ? 'tab tab--on' : 'tab'}
          onClick={() => onChange(tab.id)}
        >
          {tab.label}
          {tab.count !== undefined && <span className="tab__count">{tab.count}</span>}
        </button>
      ))}
    </div>
  );
}

export function Drawer({ title, onClose, children }: { title: ReactNode; onClose: () => void; children: ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <aside className="drawer" role="dialog" aria-modal="true">
        <header className="drawer__head">
          <h2>{title}</h2>
          <button className="icon-btn" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>
        <div className="drawer__body">{children}</div>
      </aside>
    </div>
  );
}

export function Modal({ title, onClose, children }: { title: ReactNode; onClose: () => void; children: ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="overlay overlay--center" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true">
        <header className="drawer__head">
          <h2>{title}</h2>
          <button className="icon-btn" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>
        <div className="modal__body">{children}</div>
      </div>
    </div>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field__label">{label}</span>
      {children}
      {hint && <span className="field__hint">{hint}</span>}
    </label>
  );
}

/** A value shown once, with a copy button: secrets the broker will not show again. */
export function Secret({ label: name, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="secret">
      <span className="secret__label">{name}</span>
      <code className="secret__value">{value}</code>
      <button
        className="btn btn--ghost btn--sm"
        onClick={() => {
          void navigator.clipboard?.writeText(value).then(() => {
            setCopied(true);
            window.setTimeout(() => setCopied(false), 1500);
          });
        }}
      >
        {copied ? 'Copied' : 'Copy'}
      </button>
    </div>
  );
}

export function QrCode({ text, size = 168 }: { text: string; size?: number }) {
  const [src, setSrc] = useState<string>();
  useEffect(() => {
    let live = true;
    void QRCode.toDataURL(text, { margin: 1, width: size, color: { dark: '#0b1017', light: '#e2e9f3' } }).then(
      (url) => live && setSrc(url),
    );
    return () => {
      live = false;
    };
  }, [text, size]);
  return src ? <img className="qr" src={src} width={size} height={size} alt="TOTP QR code" /> : <div className="qr" />;
}

export function Kv({ rows }: { rows: [ReactNode, ReactNode][] }) {
  return (
    <dl className="kv">
      {rows.map(([k, v], i) => (
        <div key={i} className="kv__row">
          <dt>{k}</dt>
          <dd>{v}</dd>
        </div>
      ))}
    </dl>
  );
}
