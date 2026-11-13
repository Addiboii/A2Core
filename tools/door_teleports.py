"""Exit doors' Teleport.dat targets -> tools/data/map-events.json "teleports".

    python tools/door_teleports.py research/local/tables-<stamp>

An instance layer's exit door is an EnvObjData object with Usage MoveMap whose UsageValue names a Teleport.dat row
(E_D0_Starter_HQ003_Warehouse_002 -> World_D_Starter_HQ003_Exit). The row names the target map and the MapData teleport
actor; the actor's location is in that map's MapData TeleportDataMap (World_D_Starter_HQ003_Exit on map 1100 at
32096.6, 17616.2, 9320.8). map_events.py only resolved the names PCTeleport directives use, so 84 exit doors had no
target and did nothing (session-notes "exit door: no point World_D_Starter_HQ003_Exit for map 110004", 2026-11-09).
This adds every MoveMap / MovemapCondition target (and any PCTeleport name still missing) found in the client table
export (Table_Teleport.json + Map_*_MapData.json); rows whose actor has no location in the export are left out.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/map-events.json'
ENV = ROOT / 'tools/data/env-usages.json'


def mapdata_teleports(tables: Path):
    """MapId -> {actor/label name: [x, y, z]} from every exported MapData."""
    out = {}
    for path in sorted(tables.glob('Map_*_MapData.json')):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        map_id = data.get('MapId')
        points = {}
        for entry in data.get('TeleportDataMap') or []:
            loc = entry['Value']['Location']
            at = [round(loc['X'], 1), round(loc['Y'], 1), round(loc['Z'], 1)]
            points[entry['Key']] = at
            points.setdefault(entry['Value'].get('LabelName') or entry['Key'], at)
        if map_id is not None:
            out.setdefault(int(map_id), {}).update(points)
    return out


def wanted_names(me, env):
    names = {u['value'] for u in env['objects'].values() if u.get('usage') in ('MoveMap', 'MovemapCondition') and u.get('value')}
    for event in me['events'].values():
        own = me['points'].get(str(event['map']), {})
        for name in re.findall(r'"t": "PCTeleport", "v": \["([^"]*)"', json.dumps(event)):
            if name and name not in own:
                names.add(name)
    return names


def main():
    tables = Path(sys.argv[1])
    me = json.loads(DATA.read_text(encoding='utf-8'))
    env = json.loads(ENV.read_text(encoding='utf-8'))
    rows = {r['Name']: (r['MapId']['Value'], r['ActorName'])
            for r in json.loads((tables / 'Table_Teleport.json').read_text(encoding='utf-8'))['Properties']['Data']}
    actors = mapdata_teleports(tables)
    teleports = me.setdefault('teleports', {})
    added, missing = 0, []
    for name in sorted(wanted_names(me, env)):
        if name in teleports or name not in rows:
            continue
        map_id, actor = rows[name]
        at = actors.get(map_id, {}).get(actor) or actors.get(map_id, {}).get(name)
        if at is None and actor != 'PlayerStart':
            missing.append(name)
            continue
        teleports[name] = {'map': map_id, 'at': at}
        added += 1
    me['teleports'] = dict(sorted(teleports.items()))
    DATA.write_text(json.dumps(me, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    print(f'{added} teleport targets added; {len(missing)} without a location in the export: {missing[:10]}')


if __name__ == '__main__':
    main()
