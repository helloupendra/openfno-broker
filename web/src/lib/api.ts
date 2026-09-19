// Calls to the broker. The back office authenticates with the admin key; the
// trader terminal with an access token from the daily login, like any client.

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

const ADMIN_KEY = 'ofb.adminKey';
const TRADER_SESSION = 'ofb.traderSession';

function read(storage: Storage, key: string): string | null {
  try {
    return storage.getItem(key);
  } catch {
    return null;
  }
}

function write(storage: Storage, key: string, value: string | null): void {
  try {
    if (value === null) storage.removeItem(key);
    else storage.setItem(key, value);
  } catch {
    // Storage can be unavailable (private mode); the session then lasts for the page only.
  }
}

async function call<T>(path: string, init: RequestInit & { json?: unknown }): Promise<T> {
  const headers = new Headers(init.headers);
  let body = init.body;
  if (init.json !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(init.json);
  }

  let response: Response;
  try {
    response = await fetch(path, { ...init, headers, body });
  } catch {
    throw new ApiError(0, 'NETWORK', 'The broker did not answer. Is it running?');
  }

  const text = await response.text();
  const parsed: unknown = text ? safeJson(text) : null;
  if (!response.ok) {
    const error = (parsed as { error?: { code?: string; message?: string } } | null)?.error;
    throw new ApiError(response.status, error?.code ?? `HTTP_${response.status}`, error?.message ?? response.statusText);
  }
  return parsed as T;
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

// ---- back office ----------------------------------------------------------

export const adminKey = {
  get: () => read(sessionStorage, ADMIN_KEY),
  set: (key: string | null) => write(sessionStorage, ADMIN_KEY, key),
};

function adminHeaders(key?: string): HeadersInit {
  return { 'X-Admin-Key': key ?? adminKey.get() ?? '' };
}

export const admin = {
  get: <T>(path: string, key?: string) => call<T>(`/admin${path}`, { headers: adminHeaders(key) }),
  post: <T>(path: string, json: unknown) => call<T>(`/admin${path}`, { method: 'POST', headers: adminHeaders(), json }),
  put: <T>(path: string, json: unknown) => call<T>(`/admin${path}`, { method: 'PUT', headers: adminHeaders(), json }),
};

// ---- trader ---------------------------------------------------------------

export interface TraderSession {
  token: string;
  clientId: string;
  appId: string;
  expiresAt: string;
}

export const traderSession = {
  get(): TraderSession | null {
    const raw = read(sessionStorage, TRADER_SESSION);
    const session = raw ? (safeJson(raw) as TraderSession | null) : null;
    return session && new Date(session.expiresAt) > new Date() ? session : null;
  },
  set: (session: TraderSession | null) => write(sessionStorage, TRADER_SESSION, session ? JSON.stringify(session) : null),
};

function bearer(token: string): HeadersInit {
  return { Authorization: `Bearer ${token}` };
}

export const trader = {
  login: (json: { appId: string; appSecret: string; clientId: string; totp: string }) =>
    call<import('./types').SessionGrant>('/api/v1/session', { method: 'POST', json }),
  logout: (token: string) => call<null>('/api/v1/session', { method: 'DELETE', headers: bearer(token) }),
  whoami: () => call<{ ip: string | null }>('/api/v1/whoami', {}),
  get: <T>(token: string, path: string) => call<T>(`/api/v1${path}`, { headers: bearer(token) }),
  post: <T>(token: string, path: string, json: unknown) =>
    call<T>(`/api/v1${path}`, { method: 'POST', headers: bearer(token), json }),
  patch: <T>(token: string, path: string, json: unknown) =>
    call<T>(`/api/v1${path}`, { method: 'PATCH', headers: bearer(token), json }),
  delete: <T>(token: string, path: string) => call<T>(`/api/v1${path}`, { method: 'DELETE', headers: bearer(token) }),
};
