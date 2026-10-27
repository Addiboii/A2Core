// The live map: the game's own world-map painting for the map (tools/MapImageExport, served to staff by
// /api/admin/maps/{id}/image) with an arrow per player facing its heading, as the in-game map shows them.
// Image right = world +X, down = +Y (the client's WorldMap data). Maps without an image fall back to the
// subzone outlines in the same orientation. Drag to pan, wheel or buttons to zoom.
import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { apiBlobUrl } from '../api';
import { Icon } from '../ui/components';
import type { Call } from './AdminApp';
import { npcName } from './Monsters';
import type { LivePlayer, MapImage, MapImages, MapOutline, MonsterView } from './types';

const images = new Map<number, Promise<string | null>>();
const outlines = new Map<number, Promise<MapOutline | null>>();

function useImage(token: string, imageId: number | null) {
  const [url, setUrl] = useState<string | null | undefined>(undefined);
  useEffect(() => {
    if (imageId == null) { setUrl(null); return; }
    let live = true;
    let request = images.get(imageId);
    if (!request) {
      request = apiBlobUrl(`/api/admin/maps/${imageId}/image`, token);
      images.set(imageId, request);
    }
    setUrl(undefined);
    request.then(u => { if (live) setUrl(u); });
    return () => { live = false; };
  }, [token, imageId]);
  return url;
}

function useOutline(call: Call, mapId: number, wanted: boolean) {
  const [outline, setOutline] = useState<MapOutline | null>(null);
  useEffect(() => {
    if (!wanted) { setOutline(null); return; }
    let live = true;
    let request = outlines.get(mapId);
    if (!request) {
      request = call<MapOutline>(`/api/admin/maps/${mapId}/outline`).catch(() => { outlines.delete(mapId); return null; });
      outlines.set(mapId, request);
    }
    request.then(o => { if (live) setOutline(o); });
    return () => { live = false; };
  }, [call, mapId, wanted]);
  return outline;
}

/** "World_L_Starter" → "Light · Starter", "Dungeon_0005" → "Dungeon 0005". */
export function mapTitle(id: number, image?: MapImage) {
  if (!image) return `Map ${id}`;
  const parts = image.name.replace(/^World_/, '').split('_');
  const side = parts[0] === 'L' ? 'Light' : parts[0] === 'D' ? 'Dark' : null;
  const rest = (side ? parts.slice(1) : parts).join(' ');
  return side ? `${side} · ${rest || 'Main'}` : rest;
}

interface View { x: number; y: number; w: number }
type Point = [number, number];

export function WorldMap({ call, token, mapId, image, players, selected, onSelect, monsters, onMapClick, onView, clickHint }: {
  call: Call; token: string; mapId: number; image: MapImage | undefined; players: LivePlayer[];
  selected: string | null; onSelect: (name: string | null) => void;
  /** Monsters to draw (staff monster layer). */
  monsters?: MonsterView[];
  /** A click on the map itself (not a marker) in world coordinates; set while a map tool is active. */
  onMapClick?: (x: number, y: number) => void;
  /** The visible world area (centre and half the larger side, cm), reported as it changes. */
  onView?: (x: number, y: number, radius: number) => void;
  clickHint?: string;
}) {
  const imageId = image ? (image.baseMapId ?? mapId) : null;
  const url = useImage(token, imageId);
  const outline = useOutline(call, mapId, !image || url === null);
  const host = useRef<HTMLDivElement>(null);
  const [aspect, setAspect] = useState(0.62);
  const [view, setView] = useState<View | null>(null);
  const [follow, setFollow] = useState(false);
  const drag = useRef<{ id: number; x: number; y: number; view: View; moved: boolean } | null>(null);

  // Space: image pixels when the painting exists, else world centimetres (both: right = +X, down = +Y).
  const project = useCallback(([x, y]: Point): Point => {
    if (!image) return [x, y];
    const [a, b, c, d, e, f] = image.world.affine;
    return [a * x + b * y + c, d * x + e * y + f];
  }, [image]);
  const cmPerUnit = image ? 1 / image.world.affine[0] : 1;
  const unproject = useCallback(([px, py]: Point): Point => {
    if (!image) return [px, py];
    const [a, b, c, d, e, f] = image.world.affine;
    const det = a * e - b * d;
    return [(e * (px - c) - b * (py - f)) / det, (a * (py - f) - d * (px - c)) / det];
  }, [image]);

  const areas = useMemo(() => (outline?.areas ?? []).map(a => ({ name: a.name, priority: a.priority, points: a.points.map(project) })), [outline, project]);
  const dots = players.filter(p => p.position).map(p => ({ player: p, at: project([p.position![0], p.position![1]]) }));

  const full = useMemo<View | null>(() => {
    if (image) {
      const w = Math.max(image.width, image.height / aspect);
      return { x: (image.width - w) / 2, y: (image.height - w * aspect) / 2, w };
    }
    const pts = areas.flatMap(a => a.points);
    if (!pts.length) return null;
    const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
    return box(Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys), aspect, 1.1, 1000);
  }, [image, areas, aspect]);

  const fitPlayers = useCallback((): View | null => {
    if (!dots.length) return full;
    const xs = dots.map(d => d.at[0]), ys = dots.map(d => d.at[1]);
    const min = image ? Math.min(image.width, image.height) * 0.28 : 20000;
    return box(Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys), aspect, 1.6, min);
  }, [dots, full, image, aspect]);

  // Measure the frame for the view's aspect ratio.
  useEffect(() => {
    const el = host.current;
    if (!el) return;
    const observer = new ResizeObserver(() => { if (el.clientWidth > 0) setAspect(el.clientHeight / el.clientWidth); });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // A new map (or its data arriving): frame its players, else the whole map.
  const framedFor = useRef<string>('');
  useEffect(() => {
    const key = `${mapId}:${image ? 'img' : areas.length}`;
    if (framedFor.current === key || (!full && !dots.length)) return;
    framedFor.current = key;
    setView(fitPlayers());
  }, [mapId, image, areas.length, full, dots.length, fitPlayers]);

  // Following the selected player keeps it centred.
  const followed = dots.find(d => d.player.name === selected);
  useEffect(() => {
    if (follow && followed) setView(v => v && { ...v, x: followed.at[0] - v.w / 2, y: followed.at[1] - v.w * aspect / 2 });
  }, [follow, followed?.at[0], followed?.at[1], aspect]);  // eslint-disable-line react-hooks/exhaustive-deps

  const v = view ?? full;
  // Report the visible area (debounced by the caller's polling) for the monster layer.
  const viewKey = v ? `${Math.round(v.x)}:${Math.round(v.y)}:${Math.round(v.w)}:${aspect.toFixed(2)}` : '';
  useEffect(() => {
    if (!v || !onView) return;
    const [cx, cy] = unproject([v.x + v.w / 2, v.y + v.w * aspect / 2]);
    onView(cx, cy, v.w * Math.max(1, aspect) / 2 * cmPerUnit);
  }, [viewKey]);  // eslint-disable-line react-hooks/exhaustive-deps
  if (!v) {
    return <div className="worldmap worldmap-empty" ref={host}><p className="muted">{url === undefined ? 'Loading the map…' : 'No map data for this map.'}</p></div>;
  }
  const h = v.w * aspect;
  // One screen pixel in map units: markers and labels keep their on-screen size at any zoom.
  const unit = v.w / (host.current?.clientWidth || 900);
  const limit = full ? full.w * 1.5 : v.w * 4;
  const minW = image ? 40 : 2000;

  function zoomAt(factor: number, fx = 0.5, fy = 0.5) {
    setFollow(false);
    setView(cur => {
      const c = cur ?? v!;
      const w = Math.min(limit, Math.max(minW, c.w * factor));
      const ax = c.x + fx * c.w, ay = c.y + fy * c.w * aspect;
      return { x: ax - fx * w, y: ay - fy * w * aspect, w };
    });
  }
  function onWheel(event: React.WheelEvent) {
    const r = host.current!.getBoundingClientRect();
    zoomAt(event.deltaY > 0 ? 1.2 : 1 / 1.2, (event.clientX - r.left) / r.width, (event.clientY - r.top) / r.height);
  }
  function onDown(event: ReactPointerEvent) {
    if (event.button !== 0) return;
    drag.current = { id: event.pointerId, x: event.clientX, y: event.clientY, view: v!, moved: false };
  }
  function onMove(event: ReactPointerEvent) {
    const d = drag.current;
    if (!d || d.id !== event.pointerId) return;
    const dx = event.clientX - d.x, dy = event.clientY - d.y;
    if (!d.moved && Math.hypot(dx, dy) < 4) return;
    if (!d.moved) { d.moved = true; (event.currentTarget as Element).setPointerCapture(event.pointerId); setFollow(false); }
    const scale = d.view.w / host.current!.clientWidth;
    setView({ ...d.view, x: d.view.x - dx * scale, y: d.view.y - dy * scale });
  }
  function onUp(event: ReactPointerEvent) {
    const d = drag.current;
    drag.current = null;
    if (d && !d.moved && (event.target as Element).closest('.marker') === null) {
      if (onMapClick) {
        const r = host.current!.getBoundingClientRect();
        const [x, y] = unproject([v!.x + (event.clientX - r.left) / r.width * v!.w, v!.y + (event.clientY - r.top) / r.height * v!.w * aspect]);
        onMapClick(Math.round(x), Math.round(y));
      } else onSelect(null);
    }
  }

  const bar = scaleBar(v.w, host.current?.clientWidth ?? 800, cmPerUnit);
  const showLabels = dots.length <= 12 || v.w < (full?.w ?? v.w) / 6;

  return (
    <div className={`worldmap${onMapClick ? ' worldmap-tool' : ''}`} ref={host} onWheel={onWheel} onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerCancel={onUp}>
      <svg className="worldmap-svg" viewBox={`${v.x} ${v.y} ${v.w} ${h}`} preserveAspectRatio="xMidYMid slice" role="img"
        aria-label={`Map ${mapId} with ${players.length} players`}>
        {image && url && <image href={url} x={0} y={0} width={image.width} height={image.height} />}
        {(!image || url === null) && areas.map((a, i) => (
          <polygon key={i} className={`area area-p${Math.min(a.priority, 4)}`} points={a.points.map(p => p.join(',')).join(' ')}>
            <title>{a.name}</title>
          </polygon>
        ))}
        {monsters?.map(m => {
          const [mx, my] = project([m.x, m.y]);
          return (
            <g key={m.key} className={`mob${m.dead ? ' mob-dead' : ''}${m.hostile ? '' : ' mob-friendly'}${m.staff ? ' mob-staff' : ''}${m.engaged ? ' mob-engaged' : ''}`}
              transform={`translate(${mx} ${my})`}>
              <title>{npcName(m.name)} · Lv {m.level} · NPC {m.npcId}{m.dead ? ' · dead' : ''}{m.staff ? ' · placed by staff' : ''}</title>
              <circle r={unit * (m.staff ? 4 : 3)} />
            </g>
          );
        })}
        {dots.map(({ player, at }) => {
          const on = player.name === selected;
          const cls = ['marker', on && 'marker-on', player.staffRole && 'marker-staff', player.dead && 'marker-dead', player.invisible && 'marker-hidden'].filter(Boolean).join(' ');
          return (
            <g key={player.name} className={cls} transform={`translate(${at[0]} ${at[1]})`} onClick={() => onSelect(player.name)}>
              <title>{player.name} · Lv {player.level}{player.dead ? ' · dead' : ''}</title>
              {on && <circle r={unit * 16} className="marker-ring" />}
              {player.dead
                ? <path className="marker-shape" d={`M${-unit * 6} ${-unit * 6}L${unit * 6} ${unit * 6}M${unit * 6} ${-unit * 6}L${-unit * 6} ${unit * 6}`} />
                : <path className="marker-shape" transform={`rotate(${player.heading ?? 0}) scale(${unit * 5.5})`} d="M2 0L-1.3 1.45L-.55 0L-1.3 -1.45Z" />}
              {(showLabels || on) && <text y={-unit * 13} fontSize={unit * 12.5} textAnchor="middle" className="marker-label">{player.name}</text>}
            </g>
          );
        })}
      </svg>
      <div className="worldmap-tools">
        <button type="button" className="map-btn" onClick={() => zoomAt(1 / 1.5)} title="Zoom in" aria-label="Zoom in">+</button>
        <button type="button" className="map-btn" onClick={() => zoomAt(1.5)} title="Zoom out" aria-label="Zoom out">−</button>
        <button type="button" className="map-btn" onClick={() => { setFollow(false); setView(full); }} title="Whole map" aria-label="Whole map"><Icon name="fit" size={16} /></button>
        <button type="button" className="map-btn" onClick={() => { setFollow(false); setView(fitPlayers()); }} title="Frame the players" aria-label="Frame the players"><Icon name="players" size={16} /></button>
        <button type="button" className="map-btn" aria-pressed={follow} disabled={!followed} onClick={() => setFollow(f => !f)}
          title="Follow the selected player" aria-label="Follow the selected player"><Icon name="follow" size={16} /></button>
      </div>
      <svg className="compass" viewBox="-20 -20 40 40" aria-hidden="true">
        <circle r="17" className="compass-ring" />
        <path d="M0 -15L4 0L0 15L-4 0Z" className="compass-needle" />
        <path d="M0 -15L4 0L-4 0Z" className="compass-north" />
        <text y="-21" textAnchor="middle" className="compass-n">N</text>
      </svg>
      {bar && (
        <div className="scalebar">
          <svg width={bar.px} height="8" aria-hidden="true"><path d={`M1 1V7H${bar.px - 1}V1`} /></svg>
          {bar.label}
        </div>
      )}
      {image && url === undefined && <div className="worldmap-loading">Loading the map…</div>}
      {onMapClick && clickHint && <div className="worldmap-hint">{clickHint}</div>}
    </div>
  );
}

/** A view around a box: padded, at least `min` wide, at the frame's aspect ratio. */
function box(minX: number, minY: number, maxX: number, maxY: number, aspect: number, pad: number, min: number): View {
  const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
  const w = Math.max(min, (maxX - minX) * pad, (maxY - minY) * pad / aspect);
  return { x: cx - w / 2, y: cy - w * aspect / 2, w };
}

/** A round scale length near 110 screen pixels. */
function scaleBar(viewWidth: number, screenWidth: number, cmPerUnit: number) {
  if (!screenWidth) return null;
  const cmPerPx = viewWidth * cmPerUnit / screenWidth;
  const target = cmPerPx * 110;
  const steps = [1000, 2000, 5000, 10000, 20000, 50000, 100000, 200000, 500000];
  const cm = steps.reduce((best, s) => Math.abs(s - target) < Math.abs(best - target) ? s : best, steps[0]);
  return { px: Math.round(cm / cmPerPx), label: cm >= 100000 ? `${cm / 100000} km` : `${cm / 100} m` };
}

export function imageFor(images: MapImages | null, mapId: number) {
  return images?.[String(mapId)];
}
