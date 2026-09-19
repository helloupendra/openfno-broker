import { useState } from 'react';
import { LatencySummary, RequestTable } from '../components/Requests';
import { Shell } from '../components/Shell';
import { ErrorNote, Live, Panel } from '../components/ui';
import { admin } from '../lib/api';
import type { RequestEntry } from '../lib/types';
import { usePoll } from '../lib/usePoll';

type Filter = 'all' | 'refused' | 'orders';

export function Activity() {
  const requests = usePoll(() => admin.get<RequestEntry[]>('/requests?limit=500'), 2000, []);
  const [filter, setFilter] = useState<Filter>('all');
  const [client, setClient] = useState('');
  const [backOffice, setBackOffice] = useState(false);

  const shown = (requests.data ?? []).filter((r) => {
    if (!backOffice && !r.path.startsWith('/api/')) return false;
    if (filter === 'refused' && r.status < 400) return false;
    if (filter === 'orders' && !r.path.startsWith('/api/v1/orders')) return false;
    if (client && !(r.clientId ?? '').toLowerCase().includes(client.toLowerCase())) return false;
    return true;
  });

  return (
    <Shell title="Activity">
      <Panel
        title={
          <>
            <Live /> Every API call, newest first
          </>
        }
        aside={
          <div className="filters">
            <div className="segmented">
              {(['all', 'refused', 'orders'] as const).map((f) => (
                <button key={f} className={filter === f ? 'segmented__on' : ''} onClick={() => setFilter(f)}>
                  {f === 'all' ? 'All' : f === 'refused' ? 'Refused' : 'Order calls'}
                </button>
              ))}
            </div>
            <label className="check">
              <input type="checkbox" checked={backOffice} onChange={(e) => setBackOffice(e.target.checked)} /> Back-office calls
            </label>
            <input className="input input--sm mono" placeholder="Client ID" value={client} onChange={(e) => setClient(e.target.value)} />
          </div>
        }
        flush
      >
        <ErrorNote error={requests.error} />
        {requests.data && (
          <>
            <LatencySummary requests={shown} />
            <RequestTable requests={shown} showClient />
          </>
        )}
      </Panel>
      <p className="small-note">
        Failed logins appear here with no client ID: the broker cannot tie them to an account. Login bodies are never kept.
      </p>
    </Shell>
  );
}
