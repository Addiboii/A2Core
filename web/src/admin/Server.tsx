// Server health (CPU, memory, players, how busy the world is) with a short history, and (admins) a scheduled restart
// with countdown notices to the players. Polls /api/admin/server every 5 s while shown.
import { useCallback, useEffect, useState } from 'react';
import { Button, ConfirmDialog, Icon, Status, type ConfirmRequest, type StatusMessage } from '../ui/components';
import { apiBlobUrl } from '../api';
import { describe, type Call } from './AdminApp';
import { duration } from './Overview';
import type { Backups, Maintenance, PopulationPoint, ServerHealth } from './types';

const POLL_MS = 5000;
const HISTORY = 72;   // 6 minutes

interface Sample { cpu: number; players: number; memory: number; busy: number }

export function ServerView({ call, token, active, isAdmin }: { call: Call; token: string; active: boolean; isAdmin: boolean }) {
  const [health, setHealth] = useState<ServerHealth | null>(null);
  const [history, setHistory] = useState<Sample[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());

  const load = useCallback(async () => {
    try {
      const h = await call<ServerHealth>('/api/admin/server');
      setHealth(h);
      setHistory(list => [...list, { cpu: h.cpu / Math.max(1, h.cores), players: h.players, memory: h.memoryMb, busy: h.worldBusy + h.mapBusy }].slice(-HISTORY));
      setError(null);
    } catch (e) { setError(describe(e)); }
  }, [call]);
  useEffect(() => {
    if (!active) return;
    load();
    const timers = [window.setInterval(() => { if (document.visibilityState === 'visible') load(); }, POLL_MS), window.setInterval(() => setNow(Date.now()), 1000)];
    return () => timers.forEach(clearInterval);
  }, [active, load]);

  const cpu = health ? health.cpu / Math.max(1, health.cores) : 0;
  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Server</h1>
          <p className="muted">{health ? `Up for ${duration(now - Date.parse(health.startedAt))} · ${health.cores} CPU cores` : 'Loading…'}</p>
        </div>
        <Button size="sm" onClick={load}><Icon name="refresh" size={16} /> Refresh</Button>
      </header>
      <Status message={error ? { text: error, kind: 'error' } : null} />
      <div className="health-grid">
        <Gauge label="CPU" value={cpu} text={`${Math.round(cpu * 100)}%`} sub={health ? `${health.cpu.toFixed(2)} of ${health.cores} cores` : ''}
          series={history.map(s => s.cpu)} max={1} />
        <Gauge label="Players" value={health ? Math.min(1, health.players / 200) : 0} text={String(health?.players ?? '–')}
          sub={health ? `${health.connections} connections` : ''} series={history.map(s => s.players)} />
        <Gauge label="Memory" value={health ? Math.min(1, health.memoryMb / 4096) : 0} text={health ? `${health.memoryMb} MB` : '–'}
          sub={health ? `managed heap ${health.heapMb} MB · ${health.threads} threads` : ''} series={history.map(s => s.memory)} />
        <Gauge label="Game loop load" value={health ? Math.min(1, health.worldBusy + health.mapBusy) : 0}
          text={health ? `${Math.round((health.worldBusy + health.mapBusy) * 100)}%` : '–'}
          sub={health ? `waits: world ${health.worldWaitMs} ms · maps ${health.mapWaitMs} ms` : ''} series={history.map(s => s.busy)} />
      </div>
      <p className="faint small">Game loop load is the time spent running world and map work; above about 70% players start to feel lag.</p>
      <Population call={call} active={active} />
      <div className="server-cards">
        <MaintenanceCard call={call} active={active} isAdmin={isAdmin} />
        {isAdmin && <RestartCard call={call} pending={health?.restartAt ?? null} now={now} onChange={load} />}
        {isAdmin && <BackupsCard call={call} token={token} active={active} />}
      </div>
    </main>
  );
}

function Gauge({ label, value, text, sub, series, max }: { label: string; value: number; text: string; sub: string; series: number[]; max?: number }) {
  const top = max ?? Math.max(1, ...series);
  const points = series.map((v, i) => `${(i / Math.max(1, HISTORY - 1)) * 100},${30 - Math.min(1, v / top) * 28}`).join(' ');
  const level = value > 0.85 ? 'bad' : value > 0.6 ? 'warn' : 'good';
  return (
    <section className={`card gauge gauge-${level}`}>
      <span className="stat-label">{label}</span>
      <span className="gauge-value">{text}</span>
      <span className="faint small">{sub}</span>
      <svg className="spark" viewBox="0 0 100 32" preserveAspectRatio="none" aria-hidden="true">
        {series.length > 1 && <polyline points={points} />}
      </svg>
    </section>
  );
}

function RestartCard({ call, pending, now, onChange }: { call: Call; pending: string | null; now: number; onChange: () => void }) {
  const [minutes, setMinutes] = useState(10);
  const [reason, setReason] = useState('');
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  async function schedule() {
    try {
      const r = await call<{ at: string }>('/api/admin/restart', { method: 'POST', body: { minutes, reason } });
      setMessage({ text: `Restart scheduled for ${new Date(r.at).toLocaleTimeString()}.`, kind: 'ok' });
      onChange();
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }
  async function cancel() {
    try { await call('/api/admin/restart', { method: 'DELETE' }); setMessage({ text: 'Restart cancelled; players were told.', kind: 'ok' }); onChange(); }
    catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }
  const left = pending ? Date.parse(pending) - now : 0;
  return (
    <section className="card card-body stack restart-card">
      <div>
        <h2>Restart the server</h2>
        <p className="muted small">Players get notices at 10 and 5 minutes, 1 minute, 30 and 10 seconds. Everyone is saved, then the server starts again by itself (about 30 seconds).</p>
      </div>
      {pending
        ? <div className="row-between notice">
            <span>Restart in <strong className="mono">{duration(Math.max(0, left))}</strong> (at {new Date(pending).toLocaleTimeString()})</span>
            <Button variant="danger" onClick={cancel}>Cancel restart</Button>
          </div>
        : <div className="row wrap">
            <select className="select restart-minutes" value={minutes} onChange={e => setMinutes(Number(e.target.value))} aria-label="When">
              {[1, 5, 10, 15, 30, 60].map(m => <option key={m} value={m}>in {m} minute{m === 1 ? '' : 's'}</option>)}
            </select>
            <input className="input grow" placeholder="Reason shown to players (optional)" maxLength={100} value={reason} onChange={e => setReason(e.target.value)} />
            <Button variant="primary" onClick={() => setConfirm({ title: `Restart in ${minutes} minute${minutes === 1 ? '' : 's'}?`,
              body: 'Everyone online is warned and disconnected at the restart.', confirmLabel: 'Schedule restart', danger: true, onConfirm: schedule })}>
              <Icon name="restart" size={16} /> Schedule
            </Button>
          </div>}
      <Status message={message} />
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </section>
  );
}

function Population({ call, active }: { call: Call; active: boolean }) {
  const [hours, setHours] = useState(24);
  const [points, setPoints] = useState<PopulationPoint[]>([]);
  useEffect(() => {
    if (!active) return;
    call<PopulationPoint[]>('/api/admin/population?hours=' + hours).then(setPoints).catch(() => setPoints([]));
  }, [call, active, hours]);
  const start = Date.now() - hours * 3600_000;
  const max = Math.max(5, ...points.map(p => p.players));
  const peak = points.reduce<PopulationPoint | null>((best, p) => !best || p.players > best.players ? p : best, null);
  const line = points.map(p => `${((Date.parse(p.at) - start) / (hours * 3600_000)) * 1000},${100 - p.players / max * 96}`).join(' ');
  return (
    <section className="card card-body stack">
      <div className="row-between wrap">
        <div>
          <h2>Players online</h2>
          <p className="muted small">{peak ? `Peak ${peak.players} at ${new Date(peak.at).toLocaleString()}` : 'Sampled every 5 minutes; the history starts with this update.'}</p>
        </div>
        <div className="chips">
          {[24, 168, 720].map(h => <button key={h} type="button" className="chip" aria-selected={hours === h} onClick={() => setHours(h)}>{h === 24 ? '24 hours' : h === 168 ? '7 days' : '30 days'}</button>)}
        </div>
      </div>
      <svg className="pop-chart" viewBox="0 0 1000 100" preserveAspectRatio="none" aria-label="Players online over time">
        {[0.25, 0.5, 0.75].map(f => <line key={f} x1="0" x2="1000" y1={100 - f * 96} y2={100 - f * 96} className="grid-line" />)}
        {points.length > 1 && <polygon className="pop-area" points={`${line} 1000,100 0,100`} />}
        {points.length > 1 && <polyline className="pop-line" points={line} />}
      </svg>
      <div className="row-between faint small"><span>{new Date(start).toLocaleString()}</span><span>max {max}</span><span>now</span></div>
    </section>
  );
}

function MaintenanceCard({ call, active, isAdmin }: { call: Call; active: boolean; isAdmin: boolean }) {
  const [state, setState] = useState<Maintenance | null>(null);
  const [text, setText] = useState('');
  const [kick, setKick] = useState(true);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const load = useCallback(async () => {
    try { const m = await call<Maintenance>('/api/admin/maintenance'); setState(m); setText(t => t || m.message); } catch { /* optional */ }
  }, [call]);
  useEffect(() => { if (active) load(); }, [active, load]);
  async function set(enabled: boolean) {
    try {
      const r = await call<{ state: Maintenance; kicked: number }>('/api/admin/maintenance', { method: 'POST', body: { enabled, message: text, kick } });
      setState(r.state);
      setMessage({ text: enabled ? `Maintenance on${r.kicked ? `; ${r.kicked} players disconnected` : ''}. Only staff can log in.` : 'Maintenance off; everyone can log in.', kind: 'ok' });
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }
  return (
    <section className={`card card-body stack${state?.enabled ? ' maint-on' : ''}`}>
      <div className="row-between">
        <h2>Maintenance mode</h2>
        <span className={`tag ${state?.enabled ? 'tag-gm' : ''}`}>{state?.enabled ? 'On' : 'Off'}</span>
      </div>
      <p className="muted small">While on, only GameMasters and Admins can enter the game; everyone else gets a failed login.
        {state?.enabled && state.since ? ` On since ${new Date(state.since).toLocaleString()} (${state.by ?? 'staff'}).` : ''}</p>
      {isAdmin
        ? <>
            <input className="input" placeholder="Message for disconnected players (optional)" maxLength={200} value={text} onChange={e => setText(e.target.value)} />
            {!state?.enabled && <label className="check"><input type="checkbox" checked={kick} onChange={e => setKick(e.target.checked)} /><span>Disconnect players who are online now</span></label>}
            <div className="row">
              {state?.enabled
                ? <Button variant="primary" onClick={() => set(false)}>Turn maintenance off</Button>
                : <Button variant="danger" onClick={() => setConfirm({ title: 'Turn maintenance on?', body: kick ? 'Players online are disconnected now.' : 'Players online stay until they log out.',
                    confirmLabel: 'Turn on', danger: true, onConfirm: () => set(true) })}>Turn maintenance on</Button>}
            </div>
          </>
        : <p className="faint small">Only Admins can switch it.</p>}
      <Status message={message} />
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </section>
  );
}

function BackupsCard({ call, token, active }: { call: Call; token: string; active: boolean }) {
  const [data, setData] = useState<Backups | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const load = useCallback(async () => { try { setData(await call<Backups>('/api/admin/backups')); } catch { /* optional */ } }, [call]);
  useEffect(() => { if (active) load(); }, [active, load]);
  async function backup() {
    setBusy(true);
    try { const r = await call<{ name: string; length: number }>('/api/admin/backups', { method: 'POST' }); setMessage({ text: `Saved ${r.name} (${mb(r.length)}).`, kind: 'ok' }); load(); }
    catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
    finally { setBusy(false); }
  }
  async function download(name: string) {
    const url = await apiBlobUrl('/api/admin/backups/' + encodeURIComponent(name), token);
    if (!url) { setMessage({ text: 'The download failed.', kind: 'error' }); return; }
    const a = document.createElement('a');
    a.href = url; a.download = name; a.click();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
  }
  return (
    <section className="card card-body stack">
      <div className="row-between">
        <h2>Database backups</h2>
        <Button size="sm" variant="primary" busy={busy} disabled={!data?.configured} onClick={backup}>Back up now</Button>
      </div>
      <p className="muted small">A full copy of accounts and characters. It includes password hashes: keep downloads private. Updates make one automatically.</p>
      {data && !data.configured && <p className="faint small">Backups are not configured on this host.</p>}
      <ul className="backup-list">
        {data?.files.map(f => (
          <li key={f.name}>
            <span className="grow truncate mono">{f.name}</span>
            <span className="faint small">{mb(f.length)} · {new Date(f.at).toLocaleString()}</span>
            <button type="button" className="link small" onClick={() => download(f.name)}>Download</button>
          </li>
        ))}
        {data?.configured && data.files.length === 0 && <li className="empty">No backups yet.</li>}
      </ul>
      <Status message={message} />
    </section>
  );
}

const mb = (bytes: number) => `${(bytes / 1048576).toFixed(1)} MB`;
