import { useEffect, useRef, useState } from 'react';

/**
 * Listens to the broker's WebSocket stream and calls `onEvent` for each event,
 * reconnecting with a growing pause if the socket drops. Returns whether the
 * stream is connected right now.
 */
export function useStream(path: string | null, onEvent: (event: { event: string; [key: string]: unknown }) => void): boolean {
  const [connected, setConnected] = useState(false);
  const handler = useRef(onEvent);
  handler.current = onEvent;

  useEffect(() => {
    if (!path) return;
    let socket: WebSocket | null = null;
    let retry = 0;
    let timer: number | undefined;
    let stopped = false;

    const connect = () => {
      const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws';
      socket = new WebSocket(`${scheme}://${window.location.host}${path}`);
      socket.onopen = () => {
        retry = 0;
        setConnected(true);
      };
      socket.onmessage = (message) => {
        try {
          handler.current(JSON.parse(String(message.data)));
        } catch {
          // A message that is not JSON is ignored.
        }
      };
      socket.onclose = () => {
        setConnected(false);
        if (stopped) return;
        retry = Math.min(retry + 1, 6);
        timer = window.setTimeout(connect, 500 * 2 ** retry);
      };
    };

    connect();
    return () => {
      stopped = true;
      window.clearTimeout(timer);
      socket?.close();
    };
  }, [path]);

  return connected;
}
