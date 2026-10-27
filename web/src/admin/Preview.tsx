import { useEffect, useRef, useState } from 'react';
import { Button } from '../ui/components';
import type { AdminCharacter, Viewer } from './types';
import type { Wardrobe } from './useWardrobe';

/** The 3D character preview (wardrobe-viewer.js); only where the host serves the wardrobe export. */
export function Preview({ wardrobe, state, character, onViewer, onReset, onError }: {
  wardrobe: Wardrobe | null; state: 'loading' | 'ready' | 'unavailable'; character: AdminCharacter | null;
  onViewer: (viewer: Viewer) => void; onReset: () => void; onError: (text: string) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const created = useRef<Viewer | null>(null);
  const [status, setStatus] = useState('');
  const [pose, setPose] = useState<'still' | 'bind'>('still');
  // Base body: every item off, to see the model itself (staff dashboard only; no game or chat command).
  const [bare, setBare] = useState(false);
  useEffect(() => { setBare(false); }, [character?.characterId]);

  useEffect(() => {
    // The viewer has no dispose; create it once per page (StrictMode runs effects twice).
    if (!wardrobe || !host.current || created.current) return;
    created.current = wardrobe.module.createViewer(host.current, wardrobe.data, { onStatus: setStatus, onError });
    created.current.setLook('GF', []);
    onViewer(created.current);
  }, [wardrobe, onViewer, onError]);

  return (
    <section className="card preview" aria-label="Character preview">
      <div className="viewer-host" ref={host} />
      <div className="preview-top">
        <p className="preview-caption">{character ? character.name : 'No character selected'}</p>
        {state === 'ready' && (
          <div className="row">
            <Button size="sm" onClick={() => { setBare(false); onReset(); }} disabled={!character}>Show saved equipment</Button>
            <Button size="sm" variant={bare ? 'primary' : 'secondary'} aria-pressed={bare}
              title="Take every item off to see the base body model"
              onClick={() => { if (bare) { setBare(false); onReset(); } else { setBare(true); created.current?.clear(); } }}>
              Base body
            </Button>
            <select className="select select-sm pose" aria-label="Pose" value={pose}
              onChange={e => { const mode = e.target.value as 'still' | 'bind'; setPose(mode); created.current?.setPoseMode(mode); }}>
              <option value="still">Lobby idle</option>
              <option value="bind">Bind pose</option>
            </select>
          </div>
        )}
      </div>
      {state === 'unavailable' && (
        <div className="preview-empty">
          <p>3D preview unavailable</p>
          <p className="muted small">This server does not serve the wardrobe export (extracted game assets stay on development hosts).</p>
        </div>
      )}
      {state === 'loading' && <div className="preview-empty"><span className="spinner" /></div>}
      <p className="preview-status">{status}</p>
      {state === 'ready' && <p className="preview-note">Extracted meshes, approximate materials.</p>}
    </section>
  );
}
