// Announcements: a notice in the middle of every player's screen (Notice_NT) and/or a chat message, with a preview,
// colour and size, quick templates, and the recent announcements from the activity log.
import { useCallback, useEffect, useState } from 'react';
import { Button, ConfirmDialog, Icon, Status, type ConfirmRequest, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';
import type { AuditEntry } from './types';

const COLORS = [
  { hex: 'FFFFFF', name: 'White' }, { hex: 'FFD54A', name: 'Gold' }, { hex: 'FF6B6B', name: 'Red' },
  { hex: '7CE38B', name: 'Green' }, { hex: '6FC8FF', name: 'Blue' }, { hex: 'D59BFF', name: 'Purple' },
];
const TEMPLATES = [
  'The server restarts in 10 minutes. Find a safe spot.',
  'Maintenance is starting soon. Please log out safely.',
  'A world event is starting now! Gather in the main town.',
  'Welcome to the server! Report bugs to the staff in chat.',
];

export function Broadcast({ call, active }: { call: Call; active: boolean }) {
  const [text, setText] = useState('');
  const [notice, setNotice] = useState(true);
  const [chat, setChat] = useState(true);
  const [size, setSize] = useState(22);
  const [color, setColor] = useState('FFD54A');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [history, setHistory] = useState<AuditEntry[]>([]);

  const loadHistory = useCallback(async () => {
    try { setHistory(await call<AuditEntry[]>('/api/admin/audit?action=staff_announce&limit=15')); } catch { /* optional */ }
  }, [call]);
  useEffect(() => { if (active) loadHistory(); }, [active, loadHistory]);

  async function send() {
    setBusy(true);
    try {
      const r = await call<{ shown: number; chatted: number }>('/api/admin/announce', { method: 'POST', body: { text: text.trim(), notice, chat, size, color } });
      setMessage({ text: `Sent: ${notice ? `on ${r.shown} screen${r.shown === 1 ? '' : 's'}` : ''}${notice && chat ? ', ' : ''}${chat ? `${r.chatted} chat deliver${r.chatted === 1 ? 'y' : 'ies'}` : ''}.`, kind: 'ok' });
      setText('');
      loadHistory();
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
    finally { setBusy(false); }
  }

  const trimmed = text.trim();
  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Announcements</h1>
          <p className="muted">Show a message to everyone on the server: in the middle of the screen, in chat, or both.</p>
        </div>
      </header>
      <div className="broadcast-grid">
        <section className="card card-body stack">
          <div className="field">
            <label htmlFor="announce-text">Message</label>
            <textarea id="announce-text" className="input textarea" rows={4} maxLength={200} value={text} placeholder="Write the announcement…"
              onChange={e => setText(e.target.value)} />
            <small className="row-between"><span>Plain text, one line works best.</span><span className="mono">{text.length}/200</span></small>
          </div>
          <div className="templates">
            {TEMPLATES.map(t => <button key={t} type="button" className="chip" onClick={() => setText(t)}>{t}</button>)}
          </div>
          <div className="option-row">
            <label className="check"><input type="checkbox" checked={notice} onChange={e => setNotice(e.target.checked)} /><span>Middle of the screen</span></label>
            <label className="check"><input type="checkbox" checked={chat} onChange={e => setChat(e.target.checked)} /><span>Chat</span></label>
          </div>
          <fieldset className="field plain" disabled={!notice}>
            <legend>Colour</legend>
            <div className="swatches">
              {COLORS.map(c => (
                <button key={c.hex} type="button" className="swatch" aria-pressed={color === c.hex} title={c.name} aria-label={c.name}
                  onClick={() => setColor(c.hex)}><svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="10" cy="10" r="8" fill={'#' + c.hex} /></svg></button>
              ))}
            </div>
          </fieldset>
          <div className="field">
            <label htmlFor="announce-size">Text size <span className="faint">({size})</span></label>
            <input id="announce-size" type="range" min={14} max={40} value={size} disabled={!notice} onChange={e => setSize(Number(e.target.value))} />
          </div>
          <div className="row-between">
            <Status message={message} />
            <Button variant="primary" busy={busy} disabled={!trimmed || (!notice && !chat)} onClick={() => setConfirm({
              title: 'Send to everyone?', body: <q>{trimmed}</q>, confirmLabel: 'Send announcement', onConfirm: send })}>
              <Icon name="broadcast" size={16} /> Send to everyone
            </Button>
          </div>
        </section>

        <section className="stack">
          <div className="card preview-screen" aria-label="Preview">
            <span className="preview-tag">Preview</span>
            {notice && trimmed
              ? <svg className="notice-preview" viewBox="0 0 1000 120" aria-label="Notice preview">
                  <text x="500" y="70" textAnchor="middle" fontSize={size * 1.3} fill={'#' + color}
                    {...(trimmed.length * size * 0.72 > 960 ? { textLength: 960, lengthAdjust: 'spacingAndGlyphs' } : {})}>{trimmed}</text>
                </svg>
              : <p className="muted center">{notice ? 'Your notice appears here.' : 'Chat only: no notice on screen.'}</p>}
            {chat && trimmed && <div className="chat-preview"><span className="chat-from">[Server]</span> {trimmed}</div>}
          </div>
          <div className="card">
            <div className="panel-head row-between"><h2>Recent announcements</h2><Button size="sm" variant="ghost" onClick={loadHistory}><Icon name="refresh" size={15} /></Button></div>
            <ul className="history">
              {history.map(h => {
                const d = safeJson(h.details);
                return (
                  <li key={h.id}>
                    <span className="grow">{String(d.text ?? '')}</span>
                    <span className="faint small">{h.actorName ?? '–'} · {new Date(h.at).toLocaleString()}</span>
                    <button type="button" className="link small" onClick={() => setText(String(d.text ?? ''))}>Reuse</button>
                  </li>
                );
              })}
              {history.length === 0 && <li className="empty pad">No announcements yet.</li>}
            </ul>
          </div>
        </section>
      </div>
      <p className="faint small">The in-game notice style (colour format and size) is not confirmed against the official server yet; chat is the reliable fallback.</p>
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </main>
  );
}

export function safeJson(text: string): Record<string, unknown> {
  try { const v = JSON.parse(text); return v && typeof v === 'object' ? v as Record<string, unknown> : {}; } catch { return {}; }
}
