// Scheduled events: timed announcements (once or repeating) and experience / drop boosts for a time window, with
// the boosts active right now.
import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, ConfirmDialog, Icon, Status, type ConfirmRequest, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';
import type { ScheduledEvent } from './types';

type Kind = 'announce' | 'exp' | 'drop';
const KIND_LABEL: Record<Kind, string> = { announce: 'Announcement', exp: 'Experience boost', drop: 'Drop boost' };

/** A datetime-local value (local time) for a Date. */
function local(d: Date) {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function Events({ call, active }: { call: Call; active: boolean }) {
  const [events, setEvents] = useState<ScheduledEvent[]>([]);
  const [boosts, setBoosts] = useState({ exp: 1, drop: 1 });
  const [kind, setKind] = useState<Kind>('exp');
  const [starts, setStarts] = useState(() => local(new Date(Date.now() + 60_000)));
  const [ends, setEnds] = useState(() => local(new Date(Date.now() + 2 * 3600_000)));
  const [repeat, setRepeat] = useState('');
  const [text, setText] = useState('');
  const [multiplier, setMultiplier] = useState(2);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);

  const load = useCallback(async () => {
    try {
      const r = await call<{ events: ScheduledEvent[]; boosts: { exp: number; drop: number } }>('/api/admin/events');
      setEvents(r.events); setBoosts(r.boosts);
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }, [call]);
  useEffect(() => {
    if (!active) return;
    load();
    const timer = window.setInterval(load, 30000);
    return () => clearInterval(timer);
  }, [active, load]);

  async function create() {
    try {
      await call('/api/admin/events', { method: 'POST', body: {
        kind, startsAt: new Date(starts).toISOString(), endsAt: ends ? new Date(ends).toISOString() : null,
        repeatMinutes: kind === 'announce' && repeat ? Number(repeat) : null, text: text.trim(), multiplier: kind === 'announce' ? null : multiplier,
      } });
      setMessage({ text: `${KIND_LABEL[kind]} scheduled.`, kind: 'ok' });
      setText('');
      load();
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }

  const now = Date.now();
  const state = (e: ScheduledEvent) => e.cancelledAt ? 'cancelled' : e.endsAt && Date.parse(e.endsAt) <= now ? 'ended'
    : Date.parse(e.startsAt) > now ? 'scheduled' : e.kind === 'announce' && !e.repeatMinutes && e.lastRunAt ? 'sent' : 'running';
  return (
    <section className="stack">
      <div className="row-between wrap">
        <h2>Scheduled events</h2>
        <span className="row">
          <span className={`boost${boosts.exp !== 1 ? ' boost-on' : ''}`}>Experience ×{boosts.exp}</span>
          <span className={`boost${boosts.drop !== 1 ? ' boost-on' : ''}`}>Drops ×{boosts.drop}</span>
        </span>
      </div>
      <div className="events-grid">
        <div className="card card-body stack">
          <div className="chips" role="tablist" aria-label="Event kind">
            {(Object.keys(KIND_LABEL) as Kind[]).map(k => <button key={k} type="button" role="tab" className="chip" aria-selected={kind === k} onClick={() => setKind(k)}>{KIND_LABEL[k]}</button>)}
          </div>
          <div className="form-grid">
            <div className="field"><label htmlFor="ev-start">Starts</label><input id="ev-start" className="input" type="datetime-local" value={starts} onChange={e => setStarts(e.target.value)} /></div>
            <div className="field"><label htmlFor="ev-end">Ends {kind === 'announce' && <span className="faint">(optional)</span>}</label><input id="ev-end" className="input" type="datetime-local" value={ends} onChange={e => setEnds(e.target.value)} /></div>
            {kind === 'announce'
              ? <div className="field"><label htmlFor="ev-repeat">Repeat every</label>
                  <select id="ev-repeat" className="select" value={repeat} onChange={e => setRepeat(e.target.value)}>
                    <option value="">Once</option>{[15, 30, 60, 120, 240, 1440].map(m => <option key={m} value={m}>{m < 60 ? `${m} minutes` : m === 1440 ? 'day' : `${m / 60} hours`}</option>)}
                  </select></div>
              : <div className="field"><label htmlFor="ev-mult">Multiplier <span className="faint">(×{multiplier})</span></label>
                  <input id="ev-mult" type="range" min={1.1} max={5} step={0.1} value={multiplier} onChange={e => setMultiplier(Number(e.target.value))} /></div>}
          </div>
          <div className="field">
            <label htmlFor="ev-text">{kind === 'announce' ? 'Announcement' : 'Note (optional, for staff)'}</label>
            <input id="ev-text" className="input" maxLength={200} value={text} onChange={e => setText(e.target.value)}
              placeholder={kind === 'announce' ? 'Shown in the middle of the screen and in chat' : 'e.g. Weekend event'} />
          </div>
          <div className="row-between">
            <Status message={message} />
            <Button variant="primary" disabled={kind === 'announce' && !text.trim()} onClick={create}><Icon name="plus" size={16} /> Schedule</Button>
          </div>
          {kind !== 'announce' && <p className="faint small">Tip: also schedule an announcement at the start so players know about the boost.</p>}
        </div>
        <div className="card">
          <ul className="event-list">
            {events.map(e => {
              const s = state(e);
              return (
                <li key={e.id} className={s === 'cancelled' || s === 'ended' || s === 'sent' ? 'row-dim' : undefined}>
                  <div className="grow">
                    <div className="row wrap">
                      <strong>{KIND_LABEL[e.kind]}{e.multiplier ? ` ×${e.multiplier}` : ''}</strong>
                      <Badge tone={s === 'running' ? 'ok' : s === 'scheduled' ? 'accent' : undefined}>{s}</Badge>
                    </div>
                    {e.text && <div className="muted">{e.text}</div>}
                    <div className="faint small">
                      {new Date(e.startsAt).toLocaleString()}{e.endsAt ? ` → ${new Date(e.endsAt).toLocaleString()}` : ''}{e.repeatMinutes ? ` · every ${e.repeatMinutes} min` : ''} · {e.createdBy ?? '–'}
                    </div>
                  </div>
                  {(s === 'scheduled' || s === 'running') && <Button size="sm" onClick={() => setConfirm({ title: 'Cancel this event?', body: e.text || KIND_LABEL[e.kind], confirmLabel: 'Cancel event', danger: true,
                    onConfirm: async () => { try { await call(`/api/admin/events/${e.id}`, { method: 'DELETE' }); load(); } catch (err) { setMessage({ text: describe(err), kind: 'error' }); } } })}>Cancel</Button>}
                </li>
              );
            })}
            {events.length === 0 && <li className="empty pad">No events scheduled.</li>}
          </ul>
        </div>
      </div>
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </section>
  );
}
