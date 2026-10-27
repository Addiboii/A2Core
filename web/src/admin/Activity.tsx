// Activity log: the audit events (staff actions, GM commands, item grants, trades, shop, loot, characters) newest
// first, filtered by kind, with paging. Character names open the character view.
import { useCallback, useEffect, useState } from 'react';
import { Button, Icon, Status } from '../ui/components';
import { describe, type Call } from './AdminApp';
import { safeJson } from './Broadcast';
import type { AuditEntry } from './types';

const FILTERS: { id: string; label: string }[] = [
  { id: '', label: 'Everything' }, { id: 'staff_', label: 'Staff actions' }, { id: 'gm_', label: 'GM commands' },
  { id: 'item_', label: 'Items' }, { id: 'exchange', label: 'Trades' }, { id: 'shop_', label: 'Shop' }, { id: 'loot_', label: 'Loot' },
  { id: 'character_', label: 'Characters' }, { id: 'guild_', label: 'Legions' }, { id: 'account_', label: 'Accounts' },
];
const PAGE = 100;

const LABELS: Record<string, string> = {
  staff_announce: 'Announcement', staff_message: 'Message to player', staff_kick: 'Kick', staff_restore: 'Revive / heal', staff_unstuck: 'Unstuck',
  staff_teleport: 'Teleport', staff_restart_scheduled: 'Restart scheduled', staff_restart_cancelled: 'Restart cancelled',
  item_granted: 'Item granted', item_sold: 'Sold to merchant', item_restored: 'Bought back', item_enchanted: 'Enchant', exchange: 'Trade',
  shop_bought: 'Shop purchase', loot_taken: 'Loot', character_created: 'Character created', character_renamed: 'Renamed',
  character_delete_requested: 'Deletion requested', guild_created: 'Legion created', guild_disbanded: 'Legion disbanded',
  account_disabled: 'Account disabled', quest_rewarded: 'Quest reward', box_opened: 'Box opened', ride_acquired: 'Pet acquired',
  equipment_changed: 'Equipment changed',
};
const label = (action: string) => LABELS[action] ?? action.replace(/^gm_/, 'GM: ').replace(/_/g, ' ');
const tone = (action: string) => action.startsWith('staff_') ? 'tag-staff' : action.startsWith('gm_') || action === 'item_granted' || action === 'account_disabled' ? 'tag-gm'
  : action === 'exchange' ? 'tag-trade' : 'tag-plain';

export function Activity({ call, active, onOpen }: { call: Call; active: boolean; onOpen: (characterId: string) => void }) {
  const [filter, setFilter] = useState('');
  const [rows, setRows] = useState<AuditEntry[]>([]);
  const [more, setMore] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (before?: number) => {
    setBusy(true);
    try {
      const page = await call<AuditEntry[]>('/api/admin/audit?' + new URLSearchParams({ action: filter, limit: String(PAGE), ...(before ? { before: String(before) } : {}) }));
      setRows(r => before ? [...r, ...page] : page);
      setMore(page.length === PAGE);
      setError(null);
    } catch (e) { setError(describe(e)); }
    finally { setBusy(false); }
  }, [call, filter]);
  useEffect(() => { if (active) load(); }, [active, load]);

  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Activity log</h1>
          <p className="muted">Everything the server records: staff actions, GM commands, grants, trades and purchases.</p>
        </div>
        <Button size="sm" onClick={() => load()} busy={busy}><Icon name="refresh" size={16} /> Refresh</Button>
      </header>
      <div className="chips" role="tablist" aria-label="Filter">
        {FILTERS.map(f => <button key={f.id} type="button" role="tab" className="chip" aria-selected={filter === f.id} onClick={() => setFilter(f.id)}>{f.label}</button>)}
      </div>
      <Status message={error ? { text: error, kind: 'error' } : null} />
      <section className="card">
        <div className="table-wrap">
          <table className="data-table">
            <thead><tr><th>When</th><th>What</th><th>Character</th><th>By</th><th>Details</th></tr></thead>
            <tbody>
              {rows.map(r => (
                <tr key={r.id}>
                  <td data-label="When" className="nowrap" title={new Date(r.at).toLocaleString()}>{ago(r.at)}</td>
                  <td data-label="What"><span className={`tag ${tone(r.action)}`}>{label(r.action)}</span></td>
                  <td data-label="Character">{r.characterId && r.characterName
                    ? <button type="button" className="link" onClick={() => onOpen(r.characterId!)}>{r.characterName}</button> : <span className="faint">–</span>}</td>
                  <td data-label="By">{r.actorName ?? <span className="faint">–</span>}</td>
                  <td data-label="Details" className="details">{summary(r.details)}</td>
                </tr>
              ))}
              {!busy && rows.length === 0 && <tr><td colSpan={5} className="empty pad">Nothing recorded yet.</td></tr>}
            </tbody>
          </table>
        </div>
        {more && <div className="pad center"><Button size="sm" busy={busy} onClick={() => load(rows[rows.length - 1]?.id)}>Load older</Button></div>}
      </section>
    </main>
  );
}

/** Identifiers shown elsewhere or of no use in a summary. */
const HIDDEN = new Set(['requestid', 'request', 'instance', 'buildid', 'characterid', 'command']);

/** "key: value" pairs of the event's details, nested objects flattened one level. */
function summary(details: string) {
  const d = safeJson(details);
  const parts: string[] = [];
  for (const [k, v] of Object.entries(d)) {
    if (v == null || v === '' || v === 0 || v === false || HIDDEN.has(k.toLowerCase())) continue;
    if (typeof v === 'object' && !Array.isArray(v)) {
      for (const [k2, v2] of Object.entries(v as Record<string, unknown>))
        if (v2 != null && v2 !== '' && v2 !== 0 && v2 !== false && !HIDDEN.has(k2.toLowerCase())) parts.push(`${k2}: ${short(v2)}`);
    } else parts.push(`${k}: ${short(v)}`);
  }
  return parts.join(' · ') || '–';
}
const short = (v: unknown) => { const s = typeof v === 'string' ? v : JSON.stringify(v); return s.length > 80 ? s.slice(0, 78) + '…' : s; };

function ago(at: string) {
  const s = Math.max(0, (Date.now() - Date.parse(at)) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return new Date(at).toLocaleDateString();
}
