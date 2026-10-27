import { useEffect, useState, type FormEvent } from 'react';
import { ApiError, requestId } from '../api';
import { Badge, Button, Tabs } from '../ui/components';
import { describe } from './AdminApp';
import type { Tools } from './Dashboard';
import { ROLE_LABEL, type AdminCharacter, type CharacterDetail } from './types';
import { iconUrl, type Wardrobe } from './useWardrobe';

const GENDER_NAME: Record<number, string> = { 1: 'Male', 2: 'Female' };

export function CharacterPanel({ tools, results, query, onQuery, onSearch, detail, onOpen, onRefresh, wardrobe }: {
  tools: Tools; results: AdminCharacter[]; query: string; onQuery: (q: string) => void; onSearch: (q: string) => void;
  detail: CharacterDetail | null; onOpen: (id: string) => void; onRefresh: () => Promise<void>; wardrobe: Wardrobe | null;
}) {
  return (
    <section className="card panel characters-panel" aria-label="Characters">
      <div className="panel-head stack-sm">
        <h2>Characters</h2>
        <form className="row" onSubmit={e => { e.preventDefault(); onSearch(query); }}>
          <input className="input" type="search" placeholder="Name, account or UUID" aria-label="Search characters" maxLength={64}
            value={query} onChange={e => onQuery(e.target.value)} />
          <Button type="submit">Search</Button>
        </form>
      </div>
      <ul className="results" aria-label="Search results">
        {results.map(row => (
          <li key={row.characterId}>
            <button type="button" className="result" aria-current={row.characterId === detail?.character.characterId}
              onClick={() => onOpen(row.characterId)}>
              <span className="result-name">{row.name}{row.accountDisabled && <Badge tone="danger">disabled</Badge>}</span>
              <span className="result-meta">Lv {row.level} · {row.accountName}</span>
            </button>
          </li>
        ))}
        {!results.length && <li className="empty">No characters match.</li>}
      </ul>
      {detail && <CharacterDetailView key={detail.character.characterId} tools={tools} detail={detail} onRefresh={onRefresh} wardrobe={wardrobe} />}
    </section>
  );
}

type Tab = 'overview' | 'inventory' | 'skills';

function CharacterDetailView({ tools, detail, onRefresh, wardrobe }:
  { tools: Tools; detail: CharacterDetail; onRefresh: () => Promise<void>; wardrobe: Wardrobe | null }) {
  const [tab, setTab] = useState<Tab>('overview');
  const c = detail.character;
  const learned = [...new Map(detail.inventory.skills.map(s => [s.skillId, s])).values()];
  return (
    <div className="detail">
      <div className="detail-head">
        <div className="row-between">
          <h3>{c.name}</h3>
          <Button size="sm" variant="ghost" onClick={() => onRefresh()}>Refresh</Button>
        </div>
        <p className="muted small">
          Level {c.level} · {detail.className ?? `Class ${c.creationId}`} · {GENDER_NAME[c.gender] ?? `Gender ${c.gender}`} · Race {c.race} · Account {c.accountName}
        </p>
      </div>
      <Tabs label="Character" value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: 'Overview' },
        { id: 'inventory', label: `Inventory (${detail.inventory.items.length})` },
        { id: 'skills', label: `Skills (${learned.length})` },
      ]} />
      <div className="detail-body">
        {tab === 'overview' && <Overview tools={tools} detail={detail} onRefresh={onRefresh} />}
        {tab === 'inventory' && <Inventory detail={detail} wardrobe={wardrobe} />}
        {tab === 'skills' && <Skills detail={detail} wardrobe={wardrobe} learned={learned} />}
      </div>
    </div>
  );
}

function Overview({ tools, detail, onRefresh }: { tools: Tools; detail: CharacterDetail; onRefresh: () => Promise<void> }) {
  const c = detail.character;
  const roles = c.accountRoles ?? [];
  const isAdmin = tools.session.profile.roles.includes('admin');
  const [level, setLevel] = useState(String(c.level));
  const [creationId, setCreationId] = useState(String(c.creationId));
  const [ascension, setAscension] = useState(String(detail.ascension || 1));
  const [busy, setBusy] = useState<string | null>(null);
  useEffect(() => { setLevel(String(c.level)); setCreationId(String(c.creationId)); setAscension(String(detail.ascension || 1)); }, [c, detail.ascension]);
  const classes = detail.classes ?? [];

  // One audited command per confirmation; the request UUID makes a retry harmless.
  function command(key: string, title: string, body: string, payload: Record<string, unknown>, done: string, logText: string) {
    tools.confirm({
      title, body, note: 'This is recorded in the audit log under your account.', confirmLabel: title,
      onConfirm: async () => {
        setBusy(key);
        try {
          await tools.call('/api/admin/commands', { method: 'POST', body: { requestId: requestId(), characterId: c.characterId, ...payload } });
          tools.notify(done, 'ok'); tools.log(logText);
          await onRefresh();
        } catch (error) { tools.fail(error); }
        finally { setBusy(null); }
      },
    });
  }

  // Staff standing is per character: a new character is a player even on a staff account.
  function toggleStaff() {
    const granted = !c.staff;
    command('staff', granted ? 'Make staff character' : 'Make player character',
      granted ? `Give ${c.name} the ${ROLE_LABEL[roles.includes('admin') ? 'admin' : 'gamemaster'] ?? 'staff'} standing in the world (name tag, builder commands, wings)? Applies at their next world entry.`
        : `Make ${c.name} a regular character again? Applies at their next world entry.`,
      { command: 'staff', staff: granted }, `${c.name} is now a ${granted ? 'staff' : 'player'} character.`, `${granted ? 'Staff' : 'Player'} character → ${c.name}`);
  }

  function submitLevel(event: FormEvent) {
    event.preventDefault();
    const value = Number(level);
    if (!Number.isInteger(value) || value < 1 || value > 100) { tools.notify('Level must be between 1 and 100.', 'error'); return; }
    if (value === c.level) return;
    command('level', 'Set level', `Set ${c.name} to level ${value}? Skills follow the new level.`, { command: 'level', level: value },
      `${c.name} is now level ${value}.`, `Level ${value} → ${c.name}`);
  }
  function submitClass(event: FormEvent) {
    event.preventDefault();
    const target = classes.find(x => x.creationId === Number(creationId));
    if (!target || target.creationId === c.creationId) return;
    command('class', 'Change class', `Change ${c.name} from ${detail.className ?? 'class ' + c.creationId} to ${target.name}? Skills follow the new class; race and appearance stay. Applies at their next world entry.`,
      { command: 'class', creationId: target.creationId }, `${c.name} is now a ${target.name}.`, `Class ${target.name} → ${c.name}`);
  }
  function submitAscension(event: FormEvent) {
    event.preventDefault();
    const grade = Number(ascension);
    if (grade === detail.ascension) return;
    command('ascension', 'Set ascension', `Set ${c.name} to ascension grade ${grade}? Applies at their next world entry.`,
      { command: 'ascension', ascension: grade }, `${c.name} is now ascension grade ${grade}.`, `Ascension ${grade} → ${c.name}`);
  }
  function toggleRole(role: string, granted: boolean) {
    const label = ROLE_LABEL[role];
    tools.confirm({
      title: granted ? `Make ${label}` : `Remove ${label}`, danger: !granted,
      body: granted ? `Give account ${c.accountName} the ${label} role?` : `Remove the ${label} role from account ${c.accountName}?`,
      note: 'Applies when the player next starts the game client. Admins cannot remove their own Admin role.',
      confirmLabel: granted ? `Make ${label}` : `Remove ${label}`,
      onConfirm: async () => {
        setBusy('role-' + role);
        try {
          await tools.call(`/api/admin/accounts/${c.accountId}/roles`, { method: 'POST', body: { role, granted } });
          tools.notify(`${granted ? 'Gave' : 'Removed'} ${label} ${granted ? 'to' : 'from'} ${c.accountName}.`, 'ok');
          tools.log(`${granted ? 'Gave' : 'Removed'} ${label} · account ${c.accountName}`);
          await onRefresh();
        } catch (error) {
          tools.notify(error instanceof ApiError && error.code === 'forbidden'
            ? 'Only admins can change roles, and an admin cannot remove their own Admin role.' : describe(error), 'error');
        } finally { setBusy(null); }
      },
    });
  }

  return (
    <div className="stack">
      <div className="stack-sm">
        <h4>Account roles</h4>
        <div className="row wrap">
          {roles.length ? roles.map(r => <Badge key={r} tone="accent">{ROLE_LABEL[r] ?? r}</Badge>) : <Badge>Player</Badge>}
          {c.accountDisabled && <Badge tone="danger">Account disabled</Badge>}
        </div>
        <h4>This character</h4>
        <div className="row wrap">
          {c.staff ? <Badge tone="accent">Staff character</Badge> : <Badge>Player character</Badge>}
          {roles.length > 0 && !c.accountDisabled && (
            <Button size="sm" variant={c.staff ? 'danger' : 'secondary'} busy={busy === 'staff'} onClick={toggleStaff}>
              {c.staff ? 'Make player character' : 'Make staff character'}
            </Button>
          )}
        </div>
        {isAdmin && !c.accountDisabled && (
          <div className="row wrap">
            {['gamemaster', 'admin'].map(role => {
              const has = roles.includes(role);
              return <Button key={role} size="sm" variant={has ? 'danger' : 'secondary'} busy={busy === 'role-' + role}
                onClick={() => toggleRole(role, !has)}>{has ? 'Remove' : 'Make'} {ROLE_LABEL[role]}</Button>;
            })}
          </div>
        )}
      </div>
      <div className="stack-sm">
        <h4>Progression</h4>
        <form className="inline-field" onSubmit={submitLevel}>
          <label htmlFor="level">Level</label>
          <input id="level" className="input input-sm" type="number" min={1} max={100} required value={level} onChange={e => setLevel(e.target.value)} />
          <Button type="submit" size="sm" busy={busy === 'level'} disabled={Number(level) === c.level}>Set</Button>
        </form>
        {classes.length > 0 && (
          <form className="inline-field" onSubmit={submitClass}>
            <label htmlFor="class">Class</label>
            <select id="class" className="select select-sm" value={creationId} onChange={e => setCreationId(e.target.value)}>
              {!classes.some(x => x.creationId === c.creationId) && <option value={c.creationId} disabled>Class {c.creationId} (no profile)</option>}
              {classes.map(x => <option key={x.creationId} value={x.creationId}>{x.name}</option>)}
            </select>
            <Button type="submit" size="sm" busy={busy === 'class'} disabled={Number(creationId) === c.creationId}>Change</Button>
          </form>
        )}
        <form className="inline-field" onSubmit={submitAscension}>
          <label htmlFor="ascension">Ascension</label>
          <select id="ascension" className="select select-sm" value={ascension} onChange={e => setAscension(e.target.value)}>
            {[1, 2, 3, 4, 5, 6].map(g => <option key={g} value={g}>Grade {g}</option>)}
          </select>
          <Button type="submit" size="sm" busy={busy === 'ascension'} disabled={Number(ascension) === detail.ascension}>Set</Button>
        </form>
      </div>
    </div>
  );
}

function Inventory({ detail, wardrobe }: { detail: CharacterDetail; wardrobe: Wardrobe | null }) {
  // Equipped first in slot order, then everything else in storage order.
  const rank = (location: string, slot: number | null) => location === 'equipment' ? slot ?? 0 : Number.MAX_SAFE_INTEGER;
  const items = [...detail.inventory.items].sort((a, b) => rank(a.location, a.slot) - rank(b.location, b.slot));
  if (!items.length) return <p className="empty">No items.</p>;
  return (
    <ul className="inventory">
      {items.map((entry, index) => {
        const item = wardrobe?.byId.get(entry.templateId);
        return (
          <li key={entry.instanceId ?? index}>
            <ItemIcon src={iconUrl(wardrobe, item?.icon)} grade={item?.grade} />
            <div className="grow">
              <div className="truncate">{item?.name ?? `Template ${entry.templateId}`}</div>
              <div className="muted small">{entry.location === 'equipment' ? `Equipped · slot ${entry.slot}` : entry.location} · ID {entry.templateId}</div>
            </div>
            <span className="qty">×{entry.quantity.toLocaleString()}</span>
          </li>
        );
      })}
    </ul>
  );
}

function Skills({ detail, wardrobe, learned }: { detail: CharacterDetail; wardrobe: Wardrobe | null; learned: CharacterDetail['inventory']['skills'] }) {
  if (!learned.length) return <p className="empty">No skills learned.</p>;
  return (
    <div className="stack-sm">
      <p className="muted small">Skills follow the character level. Highlighted skills are on the skill bar.</p>
      <div className="skills">
        {learned.map(s => {
          const info = wardrobe?.data.skills[s.skillId];
          const name = info?.name && info.name !== 'None' ? info.name : `Skill ${s.skillId}`;
          const slotted = detail.inventory.skills.some(x => x.skillId === s.skillId && x.slot !== null);
          const title = `${name} · level ${s.level} · ID ${s.skillId}${slotted ? ' · on skill bar' : ''}`;
          const src = iconUrl(wardrobe, info?.icon);
          return src
            ? <img key={s.skillId} className={slotted ? 'slotted' : ''} src={src} alt={name} title={title} loading="lazy" />
            : <div key={s.skillId} className={`skill-chip ${slotted ? 'slotted' : ''}`} title={title}>{s.skillId}</div>;
        })}
      </div>
    </div>
  );
}

export function ItemIcon({ src, grade, size = 36 }: { src: string | null; grade?: string | null; size?: 36 | 48 | 56 }) {
  const className = `icon icon-${size} g-${grade ?? 'none'}`;
  return src ? <img className={className} src={src} alt="" loading="lazy" /> : <div className={`${className} icon-empty`} aria-hidden="true" />;
}
