// Rewards: items and Kina straight into the bag of one character, everyone online, or every character (admins), with
// an optional message on the screens of those online. Compensation and event prizes.
import { useEffect, useState } from 'react';
import { Button, ConfirmDialog, Icon, Status, type ConfirmRequest, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';
import type { CatalogItem, CatalogPage } from './types';

type Target = 'character' | 'online' | 'all';

export function Rewards({ call, isAdmin }: { call: Call; isAdmin: boolean }) {
  const [target, setTarget] = useState<Target>('online');
  const [character, setCharacter] = useState('');
  const [query, setQuery] = useState('');
  const [found, setFound] = useState<CatalogItem[]>([]);
  const [items, setItems] = useState<{ item: CatalogItem; quantity: number }[]>([]);
  const [kina, setKina] = useState(0);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);

  useEffect(() => {
    if (query.trim().length < 2) { setFound([]); return; }
    const t = window.setTimeout(() => call<CatalogPage>('/api/admin/items?' + new URLSearchParams({ q: query.trim(), limit: '12' }))
      .then(p => setFound(p.items)).catch(() => setFound([])), 250);
    return () => clearTimeout(t);
  }, [call, query]);

  async function send() {
    setBusy(true);
    try {
      const r = await call<{ characters: number; granted: number; failed: number }>('/api/admin/rewards', { method: 'POST', body: {
        target, character, kina, message, items: items.map(i => ({ templateId: i.item.templateId, quantity: i.quantity })) } });
      setStatus({ text: `Sent to ${r.characters} character${r.characters === 1 ? '' : 's'} (${r.granted} grants${r.failed ? `, ${r.failed} unavailable` : ''}).`, kind: 'ok' });
      setItems([]); setKina(0); setMessage('');
    } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
    finally { setBusy(false); }
  }

  const who = target === 'character' ? character.trim() || 'one character' : target === 'online' ? 'everyone online now' : 'every character on the server';
  const ready = (items.length > 0 || kina > 0) && (target !== 'character' || character.trim());
  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Rewards</h1>
          <p className="muted">Give items or Kina: they go straight into the bag (players online see them at once).</p>
        </div>
      </header>
      <div className="broadcast-grid">
        <section className="card card-body stack">
          <div className="field">
            <label>Who gets it</label>
            <div className="chips" role="tablist">
              <button type="button" role="tab" className="chip" aria-selected={target === 'character'} onClick={() => setTarget('character')}>One character</button>
              <button type="button" role="tab" className="chip" aria-selected={target === 'online'} onClick={() => setTarget('online')}>Everyone online</button>
              {isAdmin && <button type="button" role="tab" className="chip" aria-selected={target === 'all'} onClick={() => setTarget('all')}>Every character</button>}
            </div>
          </div>
          {target === 'character' && <div className="field"><label htmlFor="rw-char">Character name</label><input id="rw-char" className="input" value={character} onChange={e => setCharacter(e.target.value)} maxLength={64} /></div>}
          <div className="field">
            <label htmlFor="rw-search">Add an item</label>
            <input id="rw-search" className="input" type="search" placeholder="Item name or ID" value={query} onChange={e => setQuery(e.target.value)} />
            {found.length > 0 && (
              <ul className="pick-list">
                {found.map(f => (
                  <li key={f.templateId}><button type="button" className="mob-choice" disabled={items.length >= 10} onClick={() => {
                    setItems(list => list.some(i => i.item.templateId === f.templateId) ? list : [...list, { item: f, quantity: 1 }]); setQuery('');
                  }}><span>{f.name}</span><span className="faint small">{f.templateId}{f.grade ? ` · ${f.grade}` : ''}</span></button></li>
                ))}
              </ul>
            )}
          </div>
          <ul className="reward-items">
            {items.map(i => (
              <li key={i.item.templateId}>
                <span className="grow truncate">{i.item.name} <span className="faint small">{i.item.templateId}</span></span>
                <input className="input input-sm quantity" type="number" min={1} max={1000000} value={i.quantity} aria-label="Quantity"
                  onChange={e => setItems(list => list.map(x => x === i ? { ...x, quantity: Math.max(1, Number(e.target.value) || 1) } : x))} />
                <button type="button" className="icon-btn" aria-label="Remove" onClick={() => setItems(list => list.filter(x => x !== i))}>×</button>
              </li>
            ))}
          </ul>
          <div className="field"><label htmlFor="rw-kina">Kina</label><input id="rw-kina" className="input" type="number" min={0} max={100000000} value={kina} onChange={e => setKina(Math.max(0, Number(e.target.value) || 0))} /></div>
          <div className="field"><label htmlFor="rw-msg">Message on screen (optional)</label><input id="rw-msg" className="input" maxLength={200} value={message} onChange={e => setMessage(e.target.value)} placeholder="e.g. Sorry for the downtime! Enjoy." /></div>
          <div className="row-between">
            <Status message={status} />
            <Button variant="primary" busy={busy} disabled={!ready} onClick={() => setConfirm({ title: `Send to ${who}?`, confirmLabel: 'Send rewards', danger: target === 'all',
              body: <ul>{items.map(i => <li key={i.item.templateId}>{i.quantity.toLocaleString()} × {i.item.name}</li>)}{kina > 0 && <li>{kina.toLocaleString()} Kina</li>}</ul>,
              onConfirm: send })}><Icon name="plus" size={16} /> Send</Button>
          </div>
        </section>
        <section className="card card-body stack">
          <h2>How it works</h2>
          <ul className="muted help-list">
            <li>Items are added to the bag like a staff grant; there is no in-game mailbox yet.</li>
            <li>Players in the world get the item at once; others find it at their next login.</li>
            <li>“Everyone online” is decided when you press Send.</li>
            <li>Every reward is recorded in the activity log.</li>
          </ul>
        </section>
      </div>
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </main>
  );
}
