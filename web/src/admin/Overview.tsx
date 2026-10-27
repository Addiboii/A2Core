// Live overview: server health at a glance, the game map of the chosen map with every player on it, and the
// players in the world with quick staff actions (message, revive, unstuck, teleport, kick). Polls /api/admin/live
// every 2 s and /api/admin/server every 10 s while shown and the page is visible; nothing is stored.
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Badge, Button, ConfirmDialog, Icon, PromptDialog, Status, type ConfirmRequest, type PromptRequest, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';
import { ROLE_LABEL, type LiveOverview, type LivePlayer, type MapImages, type ServerHealth } from './types';
import { MonsterBar, npcName, useMonsters, type MapTool } from './Monsters';
import { imageFor, mapTitle, WorldMap } from './WorldMap';

const POLL_MS = 2000;
const HEALTH_MS = 10000;

export function Overview({ call, token, active, onOpen }: { call: Call; token: string; active: boolean; onOpen: (characterId: string) => void }) {
  const [live, setLive] = useState<LiveOverview | null>(null);
  const [health, setHealth] = useState<ServerHealth | null>(null);
  const [images, setImages] = useState<MapImages | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [selected, setSelected] = useState<string | null>(null);
  const [mapId, setMapId] = useState<number | null>(null);
  const [filter, setFilter] = useState('');
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [prompt, setPrompt] = useState<PromptRequest | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [monstersOn, setMonstersOn] = useState(false);
  const [tool, setTool] = useState<MapTool>(null);
  const [mapView, setMapView] = useState<{ x: number; y: number; r: number } | null>(null);

  const load = useCallback(async () => {
    try { setLive(await call<LiveOverview>('/api/admin/live')); setError(null); }
    catch (e) { setError(describe(e)); }
  }, [call]);
  const loadHealth = useCallback(async () => { try { setHealth(await call<ServerHealth>('/api/admin/server')); } catch { /* optional */ } }, [call]);

  useEffect(() => { call<MapImages>('/api/admin/maps').then(setImages).catch(() => setImages({})); }, [call]);
  useEffect(() => {
    if (!active) return;
    const tick = () => { if (document.visibilityState === 'visible') load(); };
    const tickHealth = () => { if (document.visibilityState === 'visible') loadHealth(); };
    tick(); tickHealth();
    const timers = [window.setInterval(tick, POLL_MS), window.setInterval(tickHealth, HEALTH_MS), window.setInterval(() => setNow(Date.now()), 1000)];
    document.addEventListener('visibilitychange', tick);
    return () => { timers.forEach(clearInterval); document.removeEventListener('visibilitychange', tick); };
  }, [active, load, loadHealth]);

  const players = live?.players ?? [];
  const byMap = useMemo(() => {
    const m = new Map<number, LivePlayer[]>();
    for (const p of players) if (p.mapId != null) m.set(p.mapId, [...(m.get(p.mapId) ?? []), p]);
    return m;
  }, [players]);
  const occupied = [...byMap].sort((a, b) => b[1].length - a[1].length || a[0] - b[0]).map(([id]) => id);
  const shown = mapId ?? occupied[0] ?? 1000;
  const fieldMaps = Object.entries(images ?? {}).filter(([id, m]) => !m.baseMapId && Number(id) < 100000).map(([id]) => Number(id)).sort((a, b) => a - b);
  const lobby = live ? Math.max(0, live.connections - players.length) : 0;
  const { monsters, reload: reloadMonsters } = useMonsters(call, shown, monstersOn && active, mapView);
  const notify = (text: string, kind: '' | 'ok' | 'error' = '') => setMessage({ text, kind });

  async function mapClick(x: number, y: number) {
    if (!tool) return;
    const current = tool;
    setTool(null);
    try {
      if (current.kind === 'clear') {
        const r = await call<{ count: number }>(`/api/admin/maps/${shown}/monsters/clear`, { method: 'POST', body: { x, y, radius: current.radius } });
        notify(`${r.count} monsters cleared within ${current.radius / 100} m.`, 'ok');
      } else {
        const r = await call<{ keys: number[]; name: string }>(`/api/admin/maps/${shown}/monsters/spawn`, { method: 'POST', body: { npcId: current.npc.npcId, x, y, count: current.count } });
        notify(`${r.keys.length} × ${npcName(r.name)} placed. They show in the game within a second.`, 'ok');
        setMonstersOn(true);
      }
      reloadMonsters();
    } catch (e) { notify(describe(e), 'error'); }
  }
  const selectedPlayer = players.find(p => p.name === selected) ?? null;
  const listed = players.filter(p => !filter || p.name.toLowerCase().includes(filter.toLowerCase()) || (p.accountName ?? '').toLowerCase().includes(filter.toLowerCase()));

  function select(name: string | null) {
    setSelected(name);
    const p = players.find(x => x.name === name);
    if (p?.mapId != null) setMapId(p.mapId);
  }

  async function act(player: LivePlayer, path: string, body: unknown, done: string) {
    try {
      await call(`/api/admin/players/${encodeURIComponent(player.name)}/${path}`, { method: 'POST', body });
      setMessage({ text: done, kind: 'ok' });
      load();
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }
  const actions = (p: LivePlayer) => ({
    message: () => setPrompt({ title: `Message ${p.name}`, body: 'Shown in the middle of their screen.', confirmLabel: 'Send',
      fields: [{ name: 'text', label: 'Message', type: 'textarea', maxLength: 200, required: true }],
      onSubmit: v => act(p, 'message', { text: v.text }, `Message sent to ${p.name}.`) }),
    restore: () => act(p, 'restore', {}, p.dead ? `${p.name} was revived.` : `${p.name}'s HP and MP were filled.`),
    unstuck: () => setConfirm({ title: `Unstuck ${p.name}?`, body: 'They are moved to the start point of their current map.', confirmLabel: 'Move them',
      onConfirm: () => act(p, 'unstuck', {}, `${p.name} was moved to the map's start point.`) }),
    teleport: () => setPrompt({ title: `Teleport ${p.name}`, body: 'Leave the position empty for the map\'s start point.', confirmLabel: 'Teleport',
      fields: [
        { name: 'map', label: 'Map ID', type: 'number', required: true, value: String(p.mapId ?? 1000) },
        { name: 'x', label: 'X (optional)', type: 'number' }, { name: 'y', label: 'Y (optional)', type: 'number' }, { name: 'z', label: 'Z (optional)', type: 'number' },
      ],
      onSubmit: v => {
        const at = v.x && v.y && v.z ? { x: Number(v.x), y: Number(v.y), z: Number(v.z) } : {};
        act(p, 'teleport', { mapId: Number(v.map), ...at }, `${p.name} is moving to map ${v.map}.`);
      } }),
    kick: () => setPrompt({ title: `Kick ${p.name}?`, body: 'They are disconnected and can log in again.', confirmLabel: 'Kick', danger: true,
      fields: [{ name: 'reason', label: 'Reason (optional)', maxLength: 120 }],
      onSubmit: v => act(p, 'kick', { reason: v.reason }, `${p.name} was kicked.`) }),
  });

  const cpuPct = health ? Math.round(health.cpu / Math.max(1, health.cores) * 100) : null;
  return (
    <main className="page overview">
      <header className="page-head">
        <div>
          <h1>Live map</h1>
          <p className="muted">{live ? `Updated ${new Date(live.serverTime).toLocaleTimeString()}` : 'Connecting…'}</p>
        </div>
        <Button size="sm" onClick={() => { load(); loadHealth(); }}><Icon name="refresh" size={16} /> Refresh</Button>
      </header>

      <div className="stats">
        <Stat label="In the world" value={live ? players.length : '–'} tone="accent" />
        <Stat label="At character select" value={live ? lobby : '–'} />
        <Stat label="Maps in use" value={live ? byMap.size : '–'} />
        <Stat label="Server CPU" value={cpuPct == null ? '–' : `${cpuPct}%`} hint={health ? `${health.cores} cores` : undefined} />
        <Stat label="Memory" value={health ? `${health.memoryMb} MB` : '–'} />
        <Stat label="Up for" value={live ? duration(now - Date.parse(live.startedAt)) : '–'} hint={health?.restartAt ? `restart at ${new Date(health.restartAt).toLocaleTimeString()}` : undefined} />
      </div>
      <Status message={error ? { text: error, kind: 'error' } : message} />

      <div className="live-grid">
        <section className="card map-card" aria-label="Map">
          <div className="map-head">
            <div className="map-tabs" role="tablist" aria-label="Maps with players">
              {occupied.map(id => (
                <button key={id} type="button" role="tab" className="map-tab" aria-selected={id === shown} onClick={() => setMapId(id)}>
                  {mapTitle(id, imageFor(images, id))} <span className="count">{byMap.get(id)?.length}</span>
                </button>
              ))}
              {occupied.length === 0 && <span className="muted small">Nobody is in the world. Browse a map:</span>}
            </div>
            <select className="select select-sm map-pick" aria-label="Show a map" value={shown} onChange={e => setMapId(Number(e.target.value))}>
              {[...new Set([shown, ...occupied, ...fieldMaps])].map(id => <option key={id} value={id}>{id} · {mapTitle(id, imageFor(images, id))}</option>)}
            </select>
          </div>
          <MonsterBar call={call} mapId={shown} on={monstersOn} onToggle={setMonstersOn} tool={tool} onTool={setTool} count={monsters.length}
            notify={notify} reload={reloadMonsters} />
          <WorldMap call={call} token={token} mapId={shown} image={imageFor(images, shown)} players={byMap.get(shown) ?? []}
            selected={selected} onSelect={select} monsters={monstersOn ? monsters : undefined}
            onMapClick={tool ? mapClick : undefined} onView={(x, y, r) => setMapView({ x, y, r })}
            clickHint={tool?.kind === 'clear' ? `Click where to clear monsters (${tool.radius / 100} m). Esc: the button again.`
              : tool?.kind === 'spawn' ? `Click where to place ${tool.count} × ${npcName(tool.npc.name)}.` : undefined} />
        </section>

        <section className="card players-card" aria-label="Players in the world">
          <div className="panel-head stack-sm">
            <div className="row-between"><h2>Players</h2><Badge>{players.length}</Badge></div>
            <input className="input input-sm" type="search" placeholder="Filter by name or account" value={filter} onChange={e => setFilter(e.target.value)} aria-label="Filter players" />
          </div>
          <ul className="player-list">
            {listed.map(p => (
              <PlayerItem key={p.characterId ?? p.name} player={p} now={now} images={images} on={p.name === selected}
                onSelect={() => select(p.name === selected ? null : p.name)} onOpen={onOpen} actions={actions(p)} />
            ))}
            {live && listed.length === 0 && <li className="empty pad">{players.length ? 'No player matches.' : 'Nobody is in the world right now.'}</li>}
          </ul>
          {!selectedPlayer && players.length > 0 && <p className="muted small pad">Click a player on the map or in the list. Drag the map to pan, scroll to zoom.</p>}
        </section>
      </div>
      <PromptDialog request={prompt} onClose={() => setPrompt(null)} />
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </main>
  );
}

function Stat({ label, value, hint, tone }: { label: string; value: string | number; hint?: string; tone?: 'accent' }) {
  return (
    <div className={`card stat${tone ? ' stat-' + tone : ''}`}>
      <span className="stat-label">{label}</span>
      <span className="stat-value">{value}</span>
      {hint && <span className="faint small">{hint}</span>}
    </div>
  );
}

function PlayerItem({ player: p, now, images, on, onSelect, onOpen, actions }: {
  player: LivePlayer; now: number; images: MapImages | null; on: boolean; onSelect: () => void; onOpen: (id: string) => void;
  actions: Record<'message' | 'restore' | 'unstuck' | 'teleport' | 'kick', () => void>;
}) {
  return (
    <li className={`player${on ? ' player-on' : ''}`}>
      <button type="button" className="player-main" onClick={onSelect} aria-expanded={on}>
        <span className={`player-dot${p.dead ? ' is-dead' : p.staffRole ? ' is-staff' : ''}`} aria-hidden="true" />
        <span className="grow">
          <span className="player-name">
            {p.name}
            {p.staffRole && <Badge tone="accent">{ROLE_LABEL[p.staffRole] ?? p.staffRole}</Badge>}
            {p.invisible && <Badge>Hidden</Badge>}
            {p.dead && <Badge tone="danger">Dead</Badge>}
          </span>
          <span className="muted small block truncate">
            Lv {p.level}{p.className ? ` ${p.className}` : ''} · {p.mapId != null ? mapTitle(p.mapId, imageFor(images, p.mapId)) : 'Loading in'}{p.area ? ` · ${area(p.area)}` : ''}
          </span>
        </span>
        <span className="player-hp" title={`HP ${p.hp.toLocaleString()} / ${p.hpMax.toLocaleString()}`}>
          <Meter value={p.hp} max={p.hpMax} tone="hp" />
          <Meter value={p.mp} max={p.mpMax} tone="mp" />
        </span>
      </button>
      {on && (
        <div className="player-more">
          <dl className="facts">
            <div><dt>Account</dt><dd>{p.accountName ?? '–'}</dd></div>
            <div><dt>Online for</dt><dd>{duration(now - Date.parse(p.inWorldSince))}</dd></div>
            <div><dt>Party</dt><dd>{p.partySize > 0 ? `${p.partySize} (lead ${p.partyLeader})` : 'Solo'}</dd></div>
            <div><dt>Position</dt><dd className="mono">{p.position ? p.position.join(', ') : '–'}</dd></div>
            {(p.speedRejects ?? 0) > 0 && <div><dt>Speed rejects</dt><dd className="warn">{p.speedRejects}</dd></div>}
          </dl>
          <div className="action-grid">
            <Button size="sm" onClick={actions.message}><Icon name="message" size={15} /> Message</Button>
            <Button size="sm" onClick={actions.restore}><Icon name="heart" size={15} /> {p.dead ? 'Revive' : 'Heal'}</Button>
            <Button size="sm" onClick={actions.unstuck}><Icon name="pin" size={15} /> Unstuck</Button>
            <Button size="sm" onClick={actions.teleport}><Icon name="teleport" size={15} /> Teleport</Button>
            {p.characterId && <Button size="sm" onClick={() => onOpen(p.characterId!)}><Icon name="open" size={15} /> Character</Button>}
            <Button size="sm" variant="danger" onClick={actions.kick}><Icon name="kick" size={15} /> Kick</Button>
          </div>
        </div>
      )}
    </li>
  );
}

function Meter({ value, max, tone }: { value: number; max: number; tone: 'hp' | 'mp' }) {
  const pct = max > 0 ? Math.max(0, Math.min(100, Math.round(value / max * 100))) : 0;
  return (
    <svg className={`meter meter-${tone}`} viewBox="0 0 100 5" preserveAspectRatio="none" aria-hidden="true">
      <rect className="meter-track" width="100" height="5" rx="2.5" />
      <rect className="meter-fill" width={pct} height="5" rx="2.5" />
    </svg>
  );
}

/** Internal subzone names (e.g. "L_A_Poeta_Village_Subzone") without the prefix and suffix noise. */
function area(name: string) {
  return name.replace(/_Subzone$/i, '').replace(/^[LD]_(A_|B_)?/, '').replace(/_/g, ' ');
}

export function duration(ms: number) {
  if (!Number.isFinite(ms) || ms < 0) return '–';
  const s = Math.floor(ms / 1000), d = Math.floor(s / 86400), h = Math.floor(s / 3600) % 24, m = Math.floor(s / 60) % 60;
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return m > 0 ? `${m}m ${s % 60}s` : `${s}s`;
}
