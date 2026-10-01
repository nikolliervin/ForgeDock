import { useCallback, useEffect, useRef, useState } from 'react';
import type { Api } from './types';

/** Serializes polling and ignores obsolete responses after navigation or unmount. */
export function usePollingResource<T>(api: Api, path: string, intervalMs = 0) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const generation = useRef(0);
  const flight = useRef<Promise<void> | null>(null);

  const refresh = useCallback(
    (force = false) => {
      if (force) {
        generation.current += 1;
        flight.current = null;
      }
      if (flight.current) return flight.current;
      const current = generation.current;
      const request = (async () => {
        try {
          const result = await api<T>(path);
          if (current === generation.current) {
            setData(result);
            setError('');
          }
        } catch (e) {
          if (current === generation.current) setError((e as Error).message);
        } finally {
          if (current === generation.current) {
            setLoading(false);
            flight.current = null;
          }
        }
      })();
      flight.current = request;
      return request;
    },
    [api, path],
  );

  useEffect(() => {
    generation.current += 1;
    flight.current = null;
    setData(undefined);
    setLoading(true);
    setError('');
    void refresh();
    const timer = intervalMs ? setInterval(() => void refresh(), intervalMs) : undefined;
    return () => {
      generation.current += 1;
      flight.current = null;
      clearInterval(timer);
    };
  }, [refresh, intervalMs]);

  return { data, error, loading, refresh };
}
