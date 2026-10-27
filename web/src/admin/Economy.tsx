// Economy and anti-cheat: Kina in circulation, the richest characters, the most held items (large stacks stand out
// for duplication checks), the level spread, new accounts and characters per day, and live speed-check rejects.
import { useCallback, useEffect, useState } from 'react';
import { Button, Icon, Status } from '../ui/components';
import { describe, type Call } from './AdminApp';
import type { Economy as EconomyData } from './types';

export function Economy({ call, active, onOpen }: { call: Call; active: boolean; onOpen: (characterId: string) => void }) {
  const [data, setData] = useState<EconomyData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { setData(await call<EconomyData>('/api/admin/economy')); setError(null); } catch (e) { setError(describe(e)); }
  }, [call]);
  useEffect(() => { if (active) load(); }, [active, load]);
  const r = data?.report;
  const maxLevel = Math.max(1, ...(r?.levels.map(l => l.count) ?? [1]));
  const lowest = r?.levels[0]?.level ?? 1, highest = Math.max(lowest + 9, r?.levels[r.levels.length - 1]?.level ?? 10);
  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Economy</h1>
          <p className="muted">Money and items in the game, growth, and players the anti-cheat checks flagged.</p>
        </div>
        <Button size="sm" onClick={load}><Icon name="refresh" size={16} /> Refresh</Button>
      </header>
      <Status message={error ? { text: error, kind: 'error' } : null} />
      <div className="stats">
        <div className="card stat stat-accent"><span className="stat-label">Kina in circulation</span><span className="stat-value">{r ? r.totalKina.toLocaleString() : '–'}</span></div>
        <div className="card stat"><span className="stat-label">Characters</span><span className="stat-value">{r?.characters ?? '–'}</span></div>
        <div className="card stat"><span className="stat-label">Accounts</span><span className="stat-value">{r?.accounts ?? '–'}</span></div>
        <div className="card stat"><span className="stat-label">Kina per character</span><span className="stat-value">{r && r.characters ? Math.round(r.totalKina / r.characters).toLocaleString() : '–'}</span></div>
      </div>
      <div className="economy-grid">
        <section className="card">
          <div className="panel-head"><h2>Richest characters</h2></div>
          <table className="data-table">
            <thead><tr><th>#</th><th>Character</th><th>Level</th><th className="num">Kina</th></tr></thead>
            <tbody>{r?.richest.map((c, i) => (
              <tr key={c.characterId}><td>{i + 1}</td><td><button type="button" className="link" onClick={() => onOpen(c.characterId)}>{c.name}</button><span className="faint small block">{c.accountName}</span></td>
                <td>{c.level}</td><td className="num mono">{c.kina.toLocaleString()}</td></tr>
            ))}</tbody>
          </table>
        </section>
        <section className="card">
          <div className="panel-head"><h2>Anti-cheat: speed rejects now</h2></div>
          <table className="data-table">
            <thead><tr><th>Character</th><th>Map</th><th className="num">Rejects</th></tr></thead>
            <tbody>
              {data?.suspects.map(s => (
                <tr key={s.name}><td>{s.characterId ? <button type="button" className="link" onClick={() => onOpen(s.characterId!)}>{s.name}</button> : s.name}<span className="faint small block">Lv {s.level}</span></td>
                  <td>{s.mapId ?? '–'}</td><td className={`num mono${s.speedRejects >= 20 ? ' warn' : ''}`}>{s.speedRejects}</td></tr>
              ))}
              {data && data.suspects.length === 0 && <tr><td colSpan={3} className="empty pad">Nobody online moved too fast.</td></tr>}
            </tbody>
          </table>
          <p className="faint small pad">Counted since each player's login: moves faster than running, sprinting or their mount allows. A few come from lag; dozens are suspicious.</p>
        </section>
        <section className="card span-2">
          <div className="panel-head"><h2>Most held items</h2></div>
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>Item</th><th className="num">Total</th><th className="num">Holders</th><th className="num">Largest stack</th></tr></thead>
              <tbody>{r?.items.map(i => (
                <tr key={i.templateId}><td>{i.name ?? 'Unknown'} <span className="faint small">{i.templateId}</span></td><td className="num mono">{i.quantity.toLocaleString()}</td>
                  <td className="num mono">{i.holders}</td><td className="num mono">{i.largestStack.toLocaleString()}</td></tr>
              ))}</tbody>
            </table>
          </div>
        </section>
        <section className="card card-body stack">
          <h2>Characters by level</h2>
          <svg className="bars" viewBox={`0 0 ${(highest - lowest + 1) * 12} 60`} preserveAspectRatio="none" aria-label="Characters by level">
            {r?.levels.map(l => <rect key={l.level} x={(l.level - lowest) * 12 + 1} width="10" y={60 - l.count / maxLevel * 58} height={l.count / maxLevel * 58}><title>Level {l.level}: {l.count}</title></rect>)}
          </svg>
          <div className="row-between faint small"><span>Lv {lowest}</span><span>Lv {highest}</span></div>
        </section>
        <section className="card card-body stack">
          <h2>New in the last 30 days</h2>
          <DayBars label="Accounts" days={r?.newAccounts ?? []} />
          <DayBars label="Characters" days={r?.newCharacters ?? []} />
        </section>
      </div>
    </main>
  );
}

function DayBars({ label, days }: { label: string; days: { day: string; count: number }[] }) {
  const today = new Date();
  const series = Array.from({ length: 30 }, (_, i) => {
    const d = new Date(today.getTime() - (29 - i) * 86400_000).toISOString().slice(0, 10);
    return { day: d, count: days.find(x => x.day === d)?.count ?? 0 };
  });
  const max = Math.max(1, ...series.map(s => s.count));
  const total = series.reduce((a, s) => a + s.count, 0);
  return (
    <div className="stack-sm">
      <div className="row-between small"><span className="muted">{label}</span><span className="mono">{total}</span></div>
      <svg className="bars bars-small" viewBox="0 0 360 40" preserveAspectRatio="none" aria-label={label}>
        {series.map((s, i) => <rect key={s.day} x={i * 12 + 1} width="10" y={40 - s.count / max * 38} height={Math.max(s.count ? 1 : 0, s.count / max * 38)}><title>{s.day}: {s.count}</title></rect>)}
      </svg>
    </div>
  );
}
