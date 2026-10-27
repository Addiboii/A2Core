import { useCallback, useEffect, useRef, useState } from 'react';
import { ConfirmDialog, Status, type ConfirmRequest, type StatusMessage } from '../ui/components';
import { describe, type Call, type Session } from './AdminApp';
import { CharacterPanel } from './Characters';
import { ItemsPanel } from './Items';
import { Preview } from './Preview';
import { GENDER, type AdminCharacter, type CharacterDetail, type Viewer } from './types';
import { useWardrobe } from './useWardrobe';

export interface LogEntry { at: Date; text: string }

/** What every panel gets: the API, status line, confirmations and the session log. */
export interface Tools {
  call: Call;
  session: Session;
  notify: (text: string, kind?: StatusMessage['kind']) => void;
  fail: (error: unknown) => void;
  confirm: (request: ConfirmRequest) => void;
  log: (text: string) => void;
}

export function Dashboard({ session, call, focus }: { session: Session; call: Call; focus?: { id: string; n: number } | null }) {
  const { wardrobe, state: wardrobeState } = useWardrobe(call);
  const [results, setResults] = useState<AdminCharacter[]>([]);
  const [query, setQuery] = useState('');
  const [detail, setDetail] = useState<CharacterDetail | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [confirmRequest, setConfirmRequest] = useState<ConfirmRequest | null>(null);
  const [entries, setEntries] = useState<LogEntry[]>([]);
  const viewer = useRef<Viewer | null>(null);

  const tools: Tools = {
    call, session,
    notify: (text, kind = '') => setMessage({ text, kind }),
    fail: error => setMessage({ text: describe(error), kind: 'error' }),
    confirm: setConfirmRequest,
    log: text => setEntries(list => [{ at: new Date(), text }, ...list].slice(0, 50)),
  };

  const search = useCallback(async (q: string) => {
    try { setResults(await call<AdminCharacter[]>('/api/admin/characters?' + new URLSearchParams({ q: q.trim() }))); }
    catch (error) { setMessage({ text: describe(error), kind: 'error' }); }
  }, [call]);
  useEffect(() => { search(''); }, [search]);
  useEffect(() => { if (!session.profile.buildId) setMessage({ text: 'No catalogue build is imported; grants are unavailable.', kind: 'error' }); }, [session]);

  async function open(characterId: string) {
    try { setDetail(await call<CharacterDetail>('/api/admin/characters/' + characterId)); }
    catch (error) { setMessage({ text: describe(error), kind: 'error' }); }
  }
  // Opened from the overview: the focus counter re-opens the same character on a second click.
  useEffect(() => { if (focus) open(focus.id); }, [focus]);  // eslint-disable-line react-hooks/exhaustive-deps

  async function refresh() {
    if (detail) await open(detail.character.characterId);
    await search(query);
  }

  // The saved look: the character's gender with its equipped items that have extracted meshes.
  const showSavedLook = useCallback(() => {
    if (!viewer.current || !wardrobe || !detail) return;
    const equipped = detail.inventory.items.filter(i => i.location === 'equipment')
      .map(i => wardrobe.byId.get(i.templateId)).filter(i => i && wardrobe.module.wearable(i)) as NonNullable<ReturnType<typeof wardrobe.byId.get>>[];
    viewer.current.setLook(GENDER[detail.character.gender] ?? 'GF', equipped, detail.appearance);
  }, [wardrobe, detail]);
  useEffect(showSavedLook, [showSavedLook]);

  return (
    <main className="dashboard">
      <CharacterPanel tools={tools} results={results} query={query} onQuery={setQuery} onSearch={search}
        detail={detail} onOpen={open} onRefresh={refresh} wardrobe={wardrobe} />
      <Preview wardrobe={wardrobe} state={wardrobeState} character={detail?.character ?? null}
        onViewer={v => { viewer.current = v; showSavedLook(); }} onReset={showSavedLook} onError={text => setMessage({ text, kind: 'error' })} />
      <section className="card panel items-panel" aria-label="Items and activity">
        <ItemsPanel tools={tools} character={detail?.character ?? null} wardrobe={wardrobe} viewer={viewer}
          onGranted={() => detail && open(detail.character.characterId)} />
        <div className="panel-footer stack-sm">
          <Status message={message} />
          <h4>This session</h4>
          {entries.length
            ? <ul className="log">{entries.map((e, i) => <li key={i}><time>{e.at.toLocaleTimeString()}</time>{e.text}</li>)}</ul>
            : <p className="empty">Changes you make appear here.</p>}
        </div>
      </section>
      <ConfirmDialog request={confirmRequest} onClose={() => setConfirmRequest(null)} />
    </main>
  );
}
