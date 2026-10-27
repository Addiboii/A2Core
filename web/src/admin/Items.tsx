import { useEffect, useState, type MutableRefObject } from 'react';
import { ApiError, requestId } from '../api';
import { Button } from '../ui/components';
import { ItemIcon } from './Characters';
import type { Tools } from './Dashboard';
import { ERRORS, type AdminCharacter, type CatalogItem, type CatalogPage, type Viewer } from './types';
import { iconUrl, type Wardrobe } from './useWardrobe';

const PAGE = 120;

interface PendingGrant { item: CatalogItem; character: AdminCharacter; command: Record<string, unknown> & { requestId: string; quantity: number } }

/** Item catalogue (the server's latest build, so every listed item can be granted) and grants. */
export function ItemsPanel({ tools, character, wardrobe, viewer, onGranted }: {
  tools: Tools; character: AdminCharacter | null; wardrobe: Wardrobe | null;
  viewer: MutableRefObject<Viewer | null>; onGranted: () => void;
}) {
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState('');
  const [page, setPage] = useState<CatalogPage>({ items: [], total: 0, categories: [] });
  const [loading, setLoading] = useState(false);
  const [selected, setSelected] = useState<CatalogItem | null>(null);
  const [previewNote, setPreviewNote] = useState('');
  const [quantity, setQuantity] = useState('1');
  const [pending, setPending] = useState<PendingGrant | null>(null);
  const [sending, setSending] = useState(false);
  const { call } = tools;

  // Debounced search; a new query or category starts from the first page.
  useEffect(() => {
    let cancelled = false;
    const timer = setTimeout(async () => {
      setLoading(true);
      try {
        const result = await call<CatalogPage>('/api/admin/items?' + new URLSearchParams({ q: query.trim(), category, limit: String(PAGE) }));
        if (!cancelled) setPage(result);
      } catch (error) { if (!cancelled) tools.fail(error); }
      finally { if (!cancelled) setLoading(false); }
    }, 250);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [query, category, call]);

  async function more() {
    setLoading(true);
    try {
      const next = await call<CatalogPage>('/api/admin/items?' + new URLSearchParams({ q: query.trim(), category, limit: String(PAGE), offset: String(page.items.length) }));
      setPage(current => ({ ...next, items: [...current.items, ...next.items] }));
    } catch (error) { tools.fail(error); }
    finally { setLoading(false); }
  }

  function select(item: CatalogItem) {
    setSelected(item); setQuantity('1');
    const look = wardrobe?.byId.get(item.templateId);
    if (look?.slot && viewer.current) {
      const shown = viewer.current.equip(look);
      setPreviewNote(shown ? `Previewed in ${wardrobe!.module.SLOT_LABEL[look.slot] ?? look.slot}` : 'No mesh for this character');
    } else setPreviewNote('');
  }

  async function send(grant: PendingGrant) {
    setSending(true); tools.notify('Granting…');
    try {
      const result = await call<{ instanceId?: string }>('/api/admin/commands', { method: 'POST', body: grant.command });
      setPending(null);
      tools.notify(`Granted ${grant.command.quantity.toLocaleString()} × ${grant.item.name} to ${grant.character.name}.`, 'ok');
      tools.log(`${grant.command.quantity.toLocaleString()} × ${grant.item.name} → ${grant.character.name}${result?.instanceId ? ` · instance ${result.instanceId}` : ''}`);
      onGranted();
    } catch (error) {
      // 4xx answers are definite rejections; anything else may have been applied, so keep the
      // same request for a retry (the server answers a repeated request ID with the first result).
      const definite = error instanceof ApiError && error.status >= 400 && error.status < 500 && error.status !== 429;
      setPending(definite ? null : grant);
      tools.fail(error);
    } finally { setSending(false); }
  }

  function grant() {
    if (!selected || !character) return;
    const count = Number(quantity);
    if (!Number.isInteger(count) || count < 1 || count > 1_000_000) { tools.notify(ERRORS.invalid_grant, 'error'); return; }
    const item = selected, target = character;
    tools.confirm({
      title: 'Grant item', confirmLabel: 'Grant',
      body: <>Grant <strong>{count.toLocaleString()} × {item.name}</strong> (ID {item.templateId}) to <strong>{target.name}</strong> ({target.accountName})?</>,
      note: 'Recorded in the audit log under your account. A player in the world receives it at once.',
      onConfirm: () => {
        const next: PendingGrant = { item, character: target, command: { requestId: requestId(), characterId: target.characterId, command: 'grant',
          buildId: tools.session.profile.buildId, templateId: item.templateId, quantity: count } };
        send(next);
      },
    });
  }

  const canGrant = Boolean(character && selected && tools.session.profile.buildId && !character.accountDisabled && !pending);
  const look = selected ? wardrobe?.byId.get(selected.templateId) : undefined;

  return (
    <div className="items">
      <div className="panel-head stack-sm">
        <h2>Grant an item</h2>
        <div className="row">
          <input className="input" type="search" placeholder="Item name or ID" aria-label="Search items" maxLength={64}
            value={query} onChange={e => setQuery(e.target.value)} />
          <select className="select category" aria-label="Category" value={category} onChange={e => setCategory(e.target.value)}>
            <option value="">All categories</option>
            {page.categories.map(c => <option key={c} value={c}>{c}</option>)}
          </select>
        </div>
        <p className="muted small">{loading ? 'Searching…' : `${page.total.toLocaleString()} items`}</p>
      </div>

      <div className="item-grid" role="list">
        {page.items.map(item => {
          const art = wardrobe?.byId.get(item.templateId);
          return (
            <button key={item.templateId} type="button" role="listitem" className="tile" aria-pressed={item.templateId === selected?.templateId}
              title={`${item.name} · ${item.category ?? '?'} · ${item.grade ?? '?'} · ID ${item.templateId}`} onClick={() => select(item)}>
              <ItemIcon src={iconUrl(wardrobe, art?.icon)} grade={item.grade} size={48} />
              <span>{item.name}</span>
            </button>
          );
        })}
      </div>
      {page.items.length < page.total && <Button size="sm" block onClick={more} busy={loading}>Show more</Button>}

      {selected && (
        <div className="selected">
          <div className="row">
            <ItemIcon src={iconUrl(wardrobe, look?.icon)} grade={selected.grade} size={56} />
            <div className="grow">
              <h3 className="truncate">{selected.name}</h3>
              <p className="muted small">{[selected.category, selected.grade, selected.level != null && `level ${selected.level}`, `ID ${selected.templateId}`].filter(Boolean).join(' · ')}</p>
              {previewNote && <p className="muted small">{previewNote}</p>}
            </div>
          </div>
          <div className="row">
            <label className="muted small" htmlFor="quantity">Quantity</label>
            <input id="quantity" className="input input-sm quantity" type="number" min={1} max={1000000} value={quantity} onChange={e => setQuantity(e.target.value)} />
            <Button variant="primary" className="grow" disabled={!canGrant} busy={sending} onClick={grant}>
              {character ? `Grant to ${character.name}` : 'Select a character first'}
            </Button>
          </div>
        </div>
      )}

      {pending && (
        <div className="notice">
          <p>The grant of {pending.item.name} to {pending.character.name} may not have completed. Retrying sends the same request, so it cannot be applied twice.</p>
          <div className="row">
            <Button size="sm" variant="primary" busy={sending} onClick={() => send(pending)}>Retry the same request</Button>
            <Button size="sm" variant="ghost" onClick={() => { setPending(null); tools.notify(''); }}>Discard</Button>
          </div>
        </div>
      )}
    </div>
  );
}

