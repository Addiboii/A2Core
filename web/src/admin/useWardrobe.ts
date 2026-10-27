import { useEffect, useState } from 'react';
import type { Call } from './AdminApp';
import type { ViewerModule, WardrobeData, WardrobeItem } from './types';

export interface Wardrobe { data: WardrobeData; module: ViewerModule; byId: Map<number, WardrobeItem> }

/**
 * The wardrobe export (icons, meshes, 3D preview). A public host serves it to signed-in staff
 * only: the session is exchanged for a wardrobe cookie first (a development host needs none).
 * Without it the dashboard still works: items come from the server catalogue.
 */
export function useWardrobe(call: Call): { wardrobe: Wardrobe | null; state: 'loading' | 'ready' | 'unavailable' } {
  const [result, setResult] = useState<{ wardrobe: Wardrobe | null; state: 'loading' | 'ready' | 'unavailable' }>({ wardrobe: null, state: 'loading' });
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        await call('/api/admin/wardrobe-access', { method: 'POST' }).catch(() => {});
        const response = await fetch('/wardrobe/data', { cache: 'no-store' });
        if (!response.ok || !response.headers.get('content-type')?.includes('json')) throw new Error('no wardrobe');
        const data = await response.json() as WardrobeData;
        // Runtime module on the host (imports three.js from the export); not part of the bundle.
        const url = '/wardrobe-viewer.js';
        const module = await import(/* @vite-ignore */ url) as ViewerModule;
        if (!cancelled) setResult({ wardrobe: { data, module, byId: new Map(data.items.map(i => [i.id, i])) }, state: 'ready' });
      } catch {
        if (!cancelled) setResult({ wardrobe: null, state: 'unavailable' });
      }
    })();
    return () => { cancelled = true; };
    // Once per signed-in dashboard (call changes only with the session).
  }, [call]);
  return result;
}

export function iconUrl(wardrobe: Wardrobe | null, icon?: string): string | null {
  return wardrobe && icon ? wardrobe.module.FILES + icon : null;
}
