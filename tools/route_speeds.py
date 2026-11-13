"""NPC route speed bonus (MapData WaypointsDataMap AdditionalMoveStat) -> tools/data/world-npcs.json routes "boost".

    python tools/route_speeds.py research/local/tables-<stamp>

A scripted route carries its own move-speed bonus (1/10000 of the NPC's speed): Robin's L0_Starter_HQ001_Robin_Waypoints_1 has
8000, which is the MoveSpeed 8000 the official 0x364b showed (~875/s on his NpcData 500). The server gave +8000 to every Companion
on any run route and nothing to anyone else, so 530 run routes without a bonus were 80 % too fast for companions (quest partners
ran off to the next area before the player could talk to them; user report 2026-11-09) and the 965 boosted routes of other NPCs
too slow. world_npcs.py writes the field on a full rebuild; this adds it from the client table export.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/world-npcs.json'


def main():
    tables = Path(sys.argv[1])
    boosts = {}
    for path in sorted(tables.glob('Map_*_MapData.json')):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        for entry in data.get('WaypointsDataMap') or []:
            value = entry['Value']
            if value.get('AdditionalMoveStat'):
                boosts[(str(data['MapId']), entry['Key'])] = int(value['AdditionalMoveStat'])
    world = json.loads(DATA.read_text(encoding='utf-8'))
    n = 0
    for map_id, routes in world['routes'].items():
        for route in routes:
            boost = boosts.get((map_id, route['name']))
            if boost:
                route['boost'] = boost
                n += 1
            else:
                route.pop('boost', None)
    DATA.write_text(json.dumps(world, separators=(',', ':')) + '\n', encoding='utf-8')
    print(n, 'routes with a speed bonus')


if __name__ == '__main__':
    main()
