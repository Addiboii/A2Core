// Monster control on the live map: show monsters around the visible area, clear an area, place copies of an NPC
// (search by name or NPC ID, then click the map), respawn every dead monster, remove monsters staff placed.
import { useCallback, useEffect, useRef, useState } from 'react';
import { Button, ConfirmDialog, type ConfirmRequest } from '../ui/components';
import { describe, type Call } from './AdminApp';
import type { MonsterView, NpcChoice } from './types';

/** Internal NPC names ("N_D1_DC_Dredgion_Snail_1") without the code prefix and index: "Dredgion Snail". */
export function npcName(name: string) {
  const parts = name.split('_');
  let i = parts[0] === 'N' || parts[0] === 'M' ? 1 : 0;
  while (i < parts.length - 1 && /^[A-Z0-9]{1,3}$/.test(parts[i])) i++;
  const rest = parts.slice(i).filter((p, j, all) => !(j === all.length - 1 && /^\d+$/.test(p)));
  return rest.join(' ').replace(/([a-z])([A-Z])/g, '$1 $2') || name;
}

export type MapTool = { kind: 'clear'; radius: number } | { kind: 'spawn'; npc: NpcChoice; count: number } | null;

/** Monsters around the visible area while the layer is on, refreshed every 3 s. */
export function useMonsters(call: Call, mapId: number, on: boolean, view: { x: number; y: number; r: number } | null) {
  const [monsters, setMonsters] = useState<MonsterView[]>([]);
  const latest = useRef(view);
  latest.current = view;
  const load = useCallback(async () => {
    const v = latest.current;
    if (!v) return;
    try {
      setMonsters(await call<MonsterView[]>(`/api/admin/maps/${mapId}/monsters?` + new URLSearchParams({ x: String(Math.round(v.x)), y: String(Math.round(v.y)), r: String(Math.round(Math.min(60000, v.r * 1.2))) })));
    } catch { setMonsters([]); }
  }, [call, mapId]);
  useEffect(() => {
    if (!on) { setMonsters([]); return; }
    load();
    const timer = window.setInterval(() => { if (document.visibilityState === 'visible') load(); }, 3000);
    return () => clearInterval(timer);
  }, [on, load]);
  // A moved view loads soon after it settles.
  useEffect(() => {
    if (!on) return;
    const t = window.setTimeout(load, 400);
    return () => clearTimeout(t);
  }, [on, load, view?.x, view?.y, view?.r]);
  return { monsters, reload: load };
}

export function MonsterBar({ call, mapId, on, onToggle, tool, onTool, count, notify, reload }: {
  call: Call; mapId: number; on: boolean; onToggle: (on: boolean) => void; tool: MapTool; onTool: (tool: MapTool) => void; count: number;
  notify: (text: string, kind?: '' | 'ok' | 'error') => void; reload: () => void;
}) {
  const [query, setQuery] = useState('');
  const [choices, setChoices] = useState<NpcChoice[]>([]);
  const [picking, setPicking] = useState(false);
  const [spawnCount, setSpawnCount] = useState(1);
  const [radius, setRadius] = useState(2500);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);

  useEffect(() => {
    if (!picking || query.trim().length < 2) { setChoices([]); return; }
    const t = window.setTimeout(() => call<NpcChoice[]>('/api/admin/npcs?q=' + encodeURIComponent(query.trim())).then(setChoices).catch(() => setChoices([])), 250);
    return () => clearTimeout(t);
  }, [call, picking, query]);

  async function post(path: string, done: (r: { count?: number; name?: string }) => string, method = 'POST') {
    try {
      const r = await call<{ count?: number; name?: string }>(`/api/admin/maps/${mapId}/monsters/${path}`, { method });
      notify(done(r), 'ok');
      reload();
    } catch (e) { notify(describe(e), 'error'); }
  }

  return (
    <div className="mob-bar">
      <label className="check"><input type="checkbox" checked={on} onChange={e => onToggle(e.target.checked)} /><span>Monsters{on ? ` (${count})` : ''}</span></label>
      <span className="mob-sep" />
      <select className="select select-sm mob-radius" value={radius} onChange={e => setRadius(Number(e.target.value))} aria-label="Clear radius">
        {[1000, 2500, 5000, 10000].map(r => <option key={r} value={r}>{r / 100} m</option>)}
      </select>
      <Button size="sm" variant={tool?.kind === 'clear' ? 'primary' : 'secondary'} onClick={() => onTool(tool?.kind === 'clear' ? null : { kind: 'clear', radius })}>Clear area</Button>
      <Button size="sm" variant={tool?.kind === 'spawn' || picking ? 'primary' : 'secondary'} onClick={() => { if (tool?.kind === 'spawn') onTool(null); else setPicking(p => !p); }}>
        {tool?.kind === 'spawn' ? `Placing ${npcName(tool.npc.name)}` : 'Spawn…'}
      </Button>
      <Button size="sm" variant="ghost" onClick={() => setConfirm({ title: 'Respawn every dead monster on this map?', body: 'They come back at once.', confirmLabel: 'Respawn',
        onConfirm: () => post('respawn', r => `${r.count} monsters respawn.`) })}>Respawn all</Button>
      <Button size="sm" variant="ghost" onClick={() => setConfirm({ title: 'Remove the monsters staff placed on this map?', body: 'Monsters from the map data stay.', confirmLabel: 'Remove', danger: true,
        onConfirm: () => post('staff', r => `${r.count} placed monsters removed.`, 'DELETE') })}>Remove placed</Button>
      {picking && (
        <div className="mob-picker card">
          <div className="row">
            <input className="input input-sm" autoFocus placeholder="NPC name or ID (e.g. wolf)" value={query} onChange={e => setQuery(e.target.value)} />
            <input className="input input-sm mob-count" type="number" min={1} max={20} value={spawnCount} onChange={e => setSpawnCount(Math.max(1, Math.min(20, Number(e.target.value) || 1)))} aria-label="How many" />
          </div>
          <ul className="mob-choices">
            {choices.map(c => (
              <li key={c.npcId}>
                <button type="button" className="mob-choice" onClick={() => { onTool({ kind: 'spawn', npc: c, count: spawnCount }); setPicking(false); }}>
                  <span>{npcName(c.name)}</span><span className="faint small">Lv {c.level} · {c.npcId}{c.hostile ? '' : ' · friendly'}</span>
                </button>
              </li>
            ))}
            {query.trim().length >= 2 && choices.length === 0 && <li className="empty pad">No placed NPC matches.</li>}
          </ul>
        </div>
      )}
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </div>
  );
}
