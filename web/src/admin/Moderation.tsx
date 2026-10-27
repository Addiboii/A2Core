// Moderation: player tickets (reports from "//report" and the account page), bans and chat mutes with a duration,
// and the chat log (30 days) with search; a chat line's sender can be muted from there.
import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, ConfirmDialog, Icon, PromptDialog, Status, Tabs, type ConfirmRequest, type PromptRequest, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';
import type { ChatLine, Sanction, Ticket } from './types';

type Tab = 'tickets' | 'sanctions' | 'chat';
const DURATIONS: { label: string; hours: number | null }[] = [
  { label: '1 hour', hours: 1 }, { label: '1 day', hours: 24 }, { label: '3 days', hours: 72 }, { label: '7 days', hours: 168 },
  { label: '30 days', hours: 720 }, { label: 'Permanent', hours: null },
];
const CATEGORY: Record<string, string> = { bug: 'Bug', player: 'Player report', stuck: 'Stuck', other: 'Other' };

export function Moderation({ call, active }: { call: Call; active: boolean }) {
  const [tab, setTab] = useState<Tab>('tickets');
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [prompt, setPrompt] = useState<PromptRequest | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const notify = (text: string, kind: StatusMessage['kind'] = '') => setMessage({ text, kind });
  const [refresh, setRefresh] = useState(0);

  async function sanction(player: string, kind: 'ban' | 'mute', hours: number | null, reason: string) {
    try {
      const r = await call<{ sanction: Sanction; kicked: number }>('/api/admin/sanctions', { method: 'POST', body: { player, kind, hours, reason } });
      notify(`${r.sanction.accountName} ${kind === 'ban' ? 'banned' : 'muted'} ${hours ? `for ${DURATIONS.find(d => d.hours === hours)?.label ?? hours + ' h'}` : 'permanently'}${r.kicked ? ` (disconnected ${r.kicked})` : ''}.`, 'ok');
      setRefresh(n => n + 1);
    } catch (e) { notify(describe(e), 'error'); }
  }
  const ask = (kind: 'ban' | 'mute', player = '') => setPrompt({
    title: kind === 'ban' ? 'Ban a player' : 'Mute a player',
    body: kind === 'ban' ? 'The whole account is disconnected and cannot enter the game until the ban ends.' : 'The account cannot chat until the mute ends.',
    confirmLabel: kind === 'ban' ? 'Ban' : 'Mute', danger: kind === 'ban',
    fields: [
      { name: 'player', label: 'Character or account name', required: true, value: player, maxLength: 64 },
      { name: 'duration', label: 'Duration: 1h, 1d, 3d, 7d, 30d or permanent', value: kind === 'ban' ? '1d' : '1h', required: true },
      { name: 'reason', label: 'Reason (shown to the player)', maxLength: 200, type: 'textarea' },
    ],
    onSubmit: v => {
      const hours = parseDuration(v.duration);
      if (hours === undefined) { notify('Use a duration like 2h, 1d, 7d or permanent.', 'error'); return; }
      sanction(v.player, kind, hours, v.reason);
    },
  });

  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Moderation</h1>
          <p className="muted">Reports from players, bans and mutes, and everything said in chat.</p>
        </div>
        <div className="row">
          <Button size="sm" onClick={() => ask('mute')}>Mute a player</Button>
          <Button size="sm" variant="danger" onClick={() => ask('ban')}>Ban a player</Button>
        </div>
      </header>
      <Tabs label="Moderation" value={tab} onChange={setTab} tabs={[{ id: 'tickets', label: 'Reports' }, { id: 'sanctions', label: 'Bans & mutes' }, { id: 'chat', label: 'Chat log' }]} />
      <Status message={message} />
      {tab === 'tickets' && <Tickets call={call} active={active} notify={notify} setPrompt={setPrompt} />}
      {tab === 'sanctions' && <Sanctions call={call} active={active} refresh={refresh} notify={notify} setConfirm={setConfirm} />}
      {tab === 'chat' && <ChatLog call={call} active={active} onMute={name => ask('mute', name)} onBan={name => ask('ban', name)} />}
      <PromptDialog request={prompt} onClose={() => setPrompt(null)} />
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </main>
  );
}

/** "2h", "1d", "30m", "permanent" → hours (null permanent); undefined when it does not parse. */
function parseDuration(text: string): number | null | undefined {
  const t = text.trim().toLowerCase();
  if (t.startsWith('perm') || t === 'forever') return null;
  const m = /^(\d+(?:\.\d+)?)\s*(m|min|h|d|w)?$/.exec(t);
  if (!m) return undefined;
  const n = Number(m[1]);
  const unit = m[2] ?? 'h';
  const hours = unit.startsWith('m') ? n / 60 : unit === 'd' ? n * 24 : unit === 'w' ? n * 168 : n;
  return hours > 0 ? hours : undefined;
}

function Tickets({ call, active, notify, setPrompt }: { call: Call; active: boolean; notify: (t: string, k?: StatusMessage['kind']) => void; setPrompt: (p: PromptRequest) => void }) {
  const [status, setStatus] = useState<'open' | 'closed' | 'all'>('open');
  const [rows, setRows] = useState<Ticket[]>([]);
  const load = useCallback(async () => {
    try { setRows(await call<Ticket[]>('/api/admin/tickets?status=' + status)); } catch (e) { notify(describe(e), 'error'); }
  }, [call, status]);  // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (active) load(); }, [active, load]);
  async function update(t: Ticket, next: 'open' | 'closed', note: string) {
    try { await call(`/api/admin/tickets/${t.id}`, { method: 'POST', body: { status: next, note } }); notify(`Report #${t.id} ${next === 'closed' ? 'closed' : 'reopened'}.`, 'ok'); load(); }
    catch (e) { notify(describe(e), 'error'); }
  }
  return (
    <section className="stack">
      <div className="row-between">
        <div className="chips">
          {(['open', 'closed', 'all'] as const).map(s => <button key={s} type="button" className="chip" aria-selected={status === s} onClick={() => setStatus(s)}>{s[0].toUpperCase() + s.slice(1)}</button>)}
        </div>
        <Button size="sm" variant="ghost" onClick={load}><Icon name="refresh" size={15} /> Refresh</Button>
      </div>
      <p className="faint small">Players report with <code>//report &lt;text&gt;</code> in chat, or from the account page.</p>
      <div className="ticket-list">
        {rows.map(t => (
          <article key={t.id} className={`card ticket ticket-${t.status}`}>
            <header className="row-between wrap">
              <span className="row wrap">
                <strong>#{t.id}</strong>
                <span className="tag">{CATEGORY[t.category] ?? t.category}</span>
                <span>{t.characterName ?? '–'}</span>
                <span className="faint small">{t.accountName ?? 'unknown account'} · {new Date(t.createdAt).toLocaleString()}</span>
              </span>
              <Badge tone={t.status === 'open' ? 'accent' : undefined}>{t.status === 'open' ? 'Open' : 'Closed'}</Badge>
            </header>
            <p className="ticket-text">{t.content}</p>
            {t.staffNote && <p className="ticket-note"><strong>{t.handledBy ?? 'Staff'}:</strong> {t.staffNote}</p>}
            <div className="row">
              {t.status === 'open'
                ? <Button size="sm" variant="primary" onClick={() => setPrompt({ title: `Close report #${t.id}`, confirmLabel: 'Close report',
                    body: 'The player sees your note on the account page.', fields: [{ name: 'note', label: 'Answer / note', type: 'textarea', maxLength: 1000, value: t.staffNote }],
                    onSubmit: v => update(t, 'closed', v.note) })}>Answer & close</Button>
                : <Button size="sm" onClick={() => update(t, 'open', t.staffNote)}>Reopen</Button>}
            </div>
          </article>
        ))}
        {rows.length === 0 && <p className="empty pad card">No {status === 'all' ? '' : status} reports.</p>}
      </div>
    </section>
  );
}

function Sanctions({ call, active, refresh, notify, setConfirm }: {
  call: Call; active: boolean; refresh: number; notify: (t: string, k?: StatusMessage['kind']) => void; setConfirm: (c: ConfirmRequest) => void;
}) {
  const [all, setAll] = useState(false);
  const [rows, setRows] = useState<Sanction[]>([]);
  const load = useCallback(async () => {
    try { setRows(await call<Sanction[]>('/api/admin/sanctions?all=' + all)); } catch (e) { notify(describe(e), 'error'); }
  }, [call, all]);  // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (active) load(); }, [active, load, refresh]);
  const now = Date.now();
  return (
    <section className="card">
      <div className="panel-head row-between">
        <label className="check"><input type="checkbox" checked={all} onChange={e => setAll(e.target.checked)} /><span>Show ended and lifted</span></label>
        <Button size="sm" variant="ghost" onClick={load}><Icon name="refresh" size={15} /></Button>
      </div>
      <div className="table-wrap">
        <table className="data-table">
          <thead><tr><th>Account</th><th>Kind</th><th>Reason</th><th>Ends</th><th>By</th><th /></tr></thead>
          <tbody>
            {rows.map(s => {
              const ended = s.revokedAt != null || (s.expiresAt != null && Date.parse(s.expiresAt) <= now);
              return (
                <tr key={s.id} className={ended ? 'row-dim' : undefined}>
                  <td data-label="Account">{s.accountName}</td>
                  <td data-label="Kind"><span className={`tag ${s.kind === 'ban' ? 'tag-gm' : 'tag-trade'}`}>{s.kind === 'ban' ? 'Ban' : 'Mute'}</span></td>
                  <td data-label="Reason">{s.reason || <span className="faint">–</span>}</td>
                  <td data-label="Ends">{s.revokedAt ? `lifted by ${s.revokedBy ?? 'staff'}` : s.expiresAt ? new Date(s.expiresAt).toLocaleString() : 'never'}</td>
                  <td data-label="By">{s.createdBy ?? '–'}<span className="faint small block">{new Date(s.createdAt).toLocaleString()}</span></td>
                  <td>{!ended && <Button size="sm" onClick={() => setConfirm({ title: `Lift the ${s.kind} of ${s.accountName}?`, body: s.reason, confirmLabel: 'Lift',
                    onConfirm: async () => { try { await call(`/api/admin/sanctions/${s.id}`, { method: 'DELETE' }); notify(`${s.kind === 'ban' ? 'Ban' : 'Mute'} lifted.`, 'ok'); load(); } catch (e) { notify(describe(e), 'error'); } } })}>Lift</Button>}</td>
                </tr>
              );
            })}
            {rows.length === 0 && <tr><td colSpan={6} className="empty pad">Nobody is banned or muted.</td></tr>}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function ChatLog({ call, active, onMute, onBan }: { call: Call; active: boolean; onMute: (name: string) => void; onBan: (name: string) => void }) {
  const [q, setQ] = useState('');
  const [sender, setSender] = useState('');
  const [rows, setRows] = useState<ChatLine[]>([]);
  const [more, setMore] = useState(false);
  const [live, setLive] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async (before?: number) => {
    try {
      const page = await call<ChatLine[]>('/api/admin/chat?' + new URLSearchParams({ q, sender, limit: '150', ...(before ? { before: String(before) } : {}) }));
      setRows(r => before ? [...r, ...page] : page);
      setMore(page.length === 150);
      setError(null);
    } catch (e) { setError(describe(e)); }
  }, [call, q, sender]);
  useEffect(() => {
    if (!active) return;
    const t = window.setTimeout(() => load(), 250);
    const timer = live ? window.setInterval(() => { if (document.visibilityState === 'visible') load(); }, 5000) : 0;
    return () => { clearTimeout(t); clearInterval(timer); };
  }, [active, load, live]);
  return (
    <section className="card">
      <div className="panel-head row wrap">
        <input className="input input-sm grow" type="search" placeholder="Search messages" value={q} onChange={e => setQ(e.target.value)} />
        <input className="input input-sm chat-sender" type="search" placeholder="Character" value={sender} onChange={e => setSender(e.target.value)} />
        <label className="check"><input type="checkbox" checked={live} onChange={e => setLive(e.target.checked)} /><span>Live</span></label>
      </div>
      <Status message={error ? { text: error, kind: 'error' } : null} />
      <ul className="chat-log">
        {rows.map(l => (
          <li key={l.id}>
            <time className="faint" title={new Date(l.at).toLocaleString()}>{new Date(l.at).toLocaleTimeString()}</time>
            <span className={`chan chan-${l.channel.split('/')[0].toLowerCase()}`}>{channel(l.channel)}</span>
            <button type="button" className="link chat-who" onClick={() => setSender(l.sender)}>{l.sender}</button>
            {l.receiver && <><span className="faint">→</span><button type="button" className="link chat-who" onClick={() => setSender(l.receiver!)}>{l.receiver}</button></>}
            <span className="chat-text">{l.content}</span>
            <span className="chat-actions">
              <button type="button" className="link small" onClick={() => onMute(l.sender)}>Mute</button>
              <button type="button" className="link small danger-link" onClick={() => onBan(l.sender)}>Ban</button>
            </span>
          </li>
        ))}
        {rows.length === 0 && <li className="empty pad">No chat messages{q || sender ? ' match' : ' yet'}.</li>}
      </ul>
      {more && <div className="pad center"><Button size="sm" onClick={() => load(rows[rows.length - 1]?.id)}>Load older</Button></div>}
    </section>
  );
}

function channel(c: string) {
  if (c === 'WHISPER') return 'Whisper';
  if (c === 'REGION/SH') return 'Shout';
  if (c === 'REGION/RG') return 'Region';
  if (c === 'WORLD') return 'World';
  return c;
}
