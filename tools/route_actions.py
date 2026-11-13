"""Waypoint actions of the client's NPC routes -> tools/data/route-actions.json.

MapData.dat WaypointsDataMap: each route (the ones map event NpcMoveToWayPoint directives name) may carry Actions, a map of
point index -> {delay_time, UseDespawn, SocialActionId, SocialActionTime}. Source: the MapData exports of
tools/dump_tables.ps1 (research/local/tables-<date>/Map_*_MapData.json; the 2026-11-09 client: 323 actions on 1,700 routes).

What the actions mean on the official server:
- An action whose decoded values are all defaults (delay 0, UseDespawn False, no social action) at the route's LAST point:
  the NPC vanishes when it arrives (0x3642 act 7, 50-100 ms after its 0x371a stop). OBSERVED for every such route walked in
  the official recordings: L0_Starter_HQ001_Elvan_Waypoints_1 (capture 20261107-011529 #29.635), N_L0_Starter_HQ002_Alsena_
  Waypoints_1 (20261107-012837 #330.132), Way_Villager01Out_Seal_Altgard_013 (20261106-105448 #266.319); no map event script
  despawns those NPCs. Routes without that action keep the NPC standing at the end (60 official walks). The export shows
  UseDespawn False for all 323 actions: the flag is lost by the decoder (a bool in the unversioned zero mask), so the default
  entry at the last point is read as the despawn. CANDIDATE: the 35 default entries at a middle point (not modelled).
- SocialActionId / SocialActionTime (49 entries, 2 on map event routes): the NPC plays the social action there for that long.
  Kept in the file, not modelled yet (not seen in a recording).

Also written: 'persistent_social', the SocialAction.dat ids of type Persistant (a pose the NPC holds: sitting, lying, sick).
An NPC told to stop one gets up first and only then walks: OBSERVED 3.8-5.4 s between the 0x9508 stop and the next 0x371b
(Elvan 1195 3.80 s, Seal_Altgard_013 villagers 1075 5.23 s, 1162 5.39 s, 1164 5.10 s, 1165 4.75 s, a Fugitive Village crowd
NPC 1072 4.20 s); server/Aion.World/RouteActions.cs keeps those times.

    python tools/route_actions.py build [--tables research/local/tables-20261109-023531]
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/route-actions.json'
TABLES = ROOT / 'research/local/tables-20261109-023531'


def route_actions(mapdata):
    """{route name: {'points': n, 'despawn': bool, 'social': [[index, social action, ms], ...]}} of one MapData table."""
    out = {}
    for entry in mapdata.get('WaypointsDataMap') or []:
        route = entry['Value']
        actions = route.get('Actions') or []
        if not actions:
            continue
        last = len(route.get('Points') or []) - 1
        item = {'points': last + 1}
        for action in actions:
            index, value = int(action['Key']), action['Value']
            social, ms = value.get('SocialActionId', 0), value.get('SocialActionTime', 0)
            if social:
                item.setdefault('social', []).append([index, social, ms])
            elif index == last and not value.get('delay_time'):
                item['despawn'] = True
            elif value.get('UseDespawn') and index == last:
                item['despawn'] = True
        if len(item) > 1:
            out[entry['Key']] = item
    return out


def persistent_social(tables=TABLES):
    path = Path(tables) / 'Table_SocialAction.json'
    if not path.exists():
        return []
    rows = [r for v in json.loads(path.read_text(encoding='utf-8'))['Properties'].values() for r in v]
    return sorted(r['ID']['Value'] for r in rows if str(r.get('Type', '')).endswith('::Persistant'))


def build(tables=TABLES):
    maps = {}
    for path in sorted(Path(tables).glob('Map_*_MapData.json')):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        found = route_actions(data)
        if found:
            maps.setdefault(str(data['MapId']), {}).update(found)
    return {'source': 'MapData.dat WaypointsDataMap Actions (%s); despawn = the default action at the last point (see tools/route_actions.py)'
                      % Path(tables).name, 'maps': maps, 'persistent_social': persistent_social(tables)}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', default=str(TABLES))
    args = parser.parse_args()
    data = build(args.tables)
    DATA.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':'), sort_keys=True) + '\n', encoding='utf-8')
    routes = sum(len(r) for r in data['maps'].values())
    despawn = sum(1 for r in data['maps'].values() for v in r.values() if v.get('despawn'))
    print('wrote %s: %d maps, %d routes with actions, %d despawn at the end' % (DATA, len(data['maps']), routes, despawn))


if __name__ == '__main__':
    main()
