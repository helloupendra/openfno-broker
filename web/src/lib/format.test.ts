import { describe, expect, it } from 'vitest';
import { ago, clock, istDateTime, istTime, istToday, label, millis, ms, price, rupees } from './format';

describe('rupees', () => {
  it('groups in lakhs and crores', () => {
    expect(rupees(195000)).toBe('₹1,95,000.00');
    expect(rupees(12345678.9)).toBe('₹1,23,45,678.90');
    expect(rupees(0)).toBe('₹0.00');
  });

  it('shows a dash for nothing', () => {
    expect(rupees(null)).toBe('—');
    expect(rupees(Number.NaN)).toBe('—');
  });
});

describe('price', () => {
  it('keeps two decimals without a currency sign', () => {
    expect(price(25000)).toBe('25,000.00');
    expect(price(0.05)).toBe('0.05');
  });
});

describe('ms', () => {
  it('is precise when small and rounded when large', () => {
    expect(ms(0.42)).toBe('0.42 ms');
    expect(ms(3.25)).toBe('3.3 ms');
    expect(ms(47.6)).toBe('48 ms');
    expect(ms(1520)).toBe('1.52 s');
  });
});

describe('millis', () => {
  it('drops the unit and keeps the precision that matters', () => {
    expect(millis(0.042)).toBe('0.04');
    expect(millis(34.56)).toBe('34.6');
    expect(millis(142.4)).toBe('142');
    expect(millis(null)).toBe('—');
  });
});

describe('IST', () => {
  // 04:35:09.123 UTC is 10:05:09.123 IST.
  const at = '2026-09-18T04:35:09.123Z';

  it('reads times on the Indian clock', () => {
    expect(istTime(at)).toBe('10:05:09');
    expect(istTime(at, true)).toBe('10:05:09.123');
    expect(istDateTime(at)).toBe('2026-09-18 10:05');
  });

  it('takes the date from IST, not UTC', () => {
    expect(istToday(new Date('2026-09-18T20:00:00Z'))).toBe('2026-09-19');
  });

  it('shortens session times', () => {
    expect(clock('09:15:00')).toBe('09:15');
    expect(clock(null)).toBe('—');
  });
});

describe('ago', () => {
  const now = new Date('2026-09-18T10:00:00Z');
  it('says how long ago in a few words', () => {
    expect(ago('2026-09-18T09:59:55Z', now)).toBe('5s ago');
    expect(ago('2026-09-18T09:30:00Z', now)).toBe('30m ago');
    expect(ago(null, now)).toBe('never');
  });
});

describe('label', () => {
  it('turns codes into words', () => {
    expect(label('TRIGGER_PENDING')).toBe('Trigger pending');
    expect(label('INSUFFICIENT_FUNDS')).toBe('Insufficient funds');
  });
});
