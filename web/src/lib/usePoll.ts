import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from './api';

export interface Polled<T> {
  data: T | undefined;
  error: ApiError | null;
  loading: boolean;
  reload: () => void;
}

/**
 * Loads now and then every `intervalMs` while the tab is visible (0 loads once).
 * A failed refresh keeps the last good data and reports the error beside it.
 */
export function usePoll<T>(load: () => Promise<T>, intervalMs: number, deps: readonly unknown[]): Polled<T> {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  const loadRef = useRef(load);
  loadRef.current = load;
  const generation = useRef(0);

  const run = useCallback(async () => {
    const mine = ++generation.current;
    try {
      const result = await loadRef.current();
      if (mine !== generation.current) return;
      setData(result);
      setError(null);
    } catch (e) {
      if (mine !== generation.current) return;
      setError(e instanceof ApiError ? e : new ApiError(0, 'UNKNOWN', String(e)));
    } finally {
      if (mine === generation.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    setLoading(true);
    setData(undefined);
    void run();
    if (intervalMs <= 0) return;
    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible') void run();
    }, intervalMs);
    return () => window.clearInterval(timer);
  }, deps);

  return { data, error, loading, reload: () => void run() };
}
