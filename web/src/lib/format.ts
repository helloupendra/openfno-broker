// Numbers, money and time the way an Indian trading desk reads them:
// lakh/crore grouping, IST clock times, milliseconds for latency.

const rupeeFormat = new Intl.NumberFormat('en-IN', {
  style: 'currency',
  currency: 'INR',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const numberFormat = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2 });

export function rupees(amount: number | null | undefined): string {
  if (amount === null || amount === undefined || Number.isNaN(amount)) return '—';
  return rupeeFormat.format(amount);
}

export function num(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return numberFormat.format(value);
}

/** A price as the order book shows it: no currency sign, two decimals. */
export function price(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—';
  return value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/** Milliseconds, precise when small and rounded when large. */
export function ms(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  if (value < 1) return `${value.toFixed(2)} ms`;
  if (value < 10) return `${value.toFixed(1)} ms`;
  if (value < 1000) return `${Math.round(value)} ms`;
  return `${(value / 1000).toFixed(2)} s`;
}

/** Milliseconds as a bare number for a column headed "ms". */
export function millis(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  if (value < 10) return value.toFixed(2);
  if (value < 100) return value.toFixed(1);
  return Math.round(value).toString();
}

const istParts = new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Asia/Kolkata',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hourCycle: 'h23',
});

function parts(date: Date): Record<string, string> {
  return Object.fromEntries(istParts.formatToParts(date).map((p) => [p.type, p.value]));
}

/** 14:05:09 in IST; with milliseconds, 14:05:09.123. */
export function istTime(iso: string | Date | null | undefined, withMs = false): string {
  if (!iso) return '—';
  const date = typeof iso === 'string' ? new Date(iso) : iso;
  if (Number.isNaN(date.getTime())) return '—';
  const p = parts(date);
  const base = `${p.hour}:${p.minute}:${p.second}`;
  return withMs ? `${base}.${String(date.getMilliseconds()).padStart(3, '0')}` : base;
}

/** 2026-09-18 14:05 IST. */
export function istDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  const p = parts(date);
  return `${p.year}-${p.month}-${p.day} ${p.hour}:${p.minute}`;
}

/** Today's date in IST as yyyy-MM-dd. */
export function istToday(now: Date = new Date()): string {
  const p = parts(now);
  return `${p.year}-${p.month}-${p.day}`;
}

/** 09:15:00 → 09:15. */
export function clock(time: string | null | undefined): string {
  return time ? time.slice(0, 5) : '—';
}

/** How long ago, in a few words. */
export function ago(iso: string | null | undefined, now: Date = new Date()): string {
  if (!iso) return 'never';
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

/** UPPER_SNAKE → Title case words, for statuses and codes shown as labels. */
export function label(value: string): string {
  return value
    .toLowerCase()
    .split('_')
    .map((word, i) => (i === 0 ? word.charAt(0).toUpperCase() + word.slice(1) : word))
    .join(' ');
}
