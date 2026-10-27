"""Kibelisks (teleport artifacts) a map places, from the client's own data.

- MapData.dat SpawnInfoList (research/local/reuse-mapdata-probe, as tools/world_npcs.py): spawn
  records with an EnvObjIdList, unique key, position and yaw.
- EnvObjData.dat (research/local/world/gameplay-tables/table-000.json): Usage
  EEnvObjectUsage::TeleportArtifact, UsageValue the artifact (TeleportArtifact_<id>) that
  quest objectives (EngraveTeleportArtifact) and the engraved list (0x9232/0x9233) name.

OBSERVED (official capture 20261013-144836, messages 401/5107-5114): the Poeta Kibelisk spawns
as 0x3634 EnvObjSpawn_NT with EnvObjData 1200081, the MapData unique key 794868238 and yaw
74.53; binding it sends 0x3638/0x363a/0x3637 then 0x9233 with 1051035.

    python tools/teleport_artifacts.py build    # writes tools/data/teleport-artifacts.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/teleport-artifacts.json'
MAPDATA = {1000: ROOT / 'research/local/reuse-mapdata-probe/table-001.json'}
ALL_MAPDATA = ROOT / 'research/local/all-mapdata-full'   # every map's MapData.dat (tools/map_points.py)
ENVOBJDATA = ROOT / 'research/local/world/gameplay-tables/table-000.json'
USAGE = 'EEnvObjectUsage::TeleportArtifact'


def walk(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from walk(value)
    elif isinstance(node, list):
        for value in node:
            yield from walk(value)


def artifact_objects(envobj_rows):
    """EnvObjData id -> artifact code for every teleport-artifact object."""
    result = {}
    for row in walk(envobj_rows):
        if row.get('Usage') == USAGE and isinstance(row.get('ID'), dict):
            code = row.get('UsageValue')
            if isinstance(code, str) and code.startswith('TeleportArtifact_'):
                result[row['ID']['Value']] = code
    return result


def placements(mapdata, objects):
    """Spawn records whose EnvObjIdList names a teleport-artifact object, one per position."""
    found = []
    for record in walk(mapdata):
        ids = [e.get('Value') for e in record.get('EnvObjIdList') or [] if isinstance(e, dict)]
        for env in ids:
            if env not in objects:
                continue
            for position in record.get('Positions') or []:
                loc, rot = position['Location'], position['Rotation']
                code = objects[env]
                found.append({
                    'name': record.get('Name'), 'env_obj': env, 'artifact': code,
                    'artifact_id': int(code.rsplit('_', 1)[1]), 'unique_key': record.get('UniqueKey', 0),
                    'position': [round(loc['X'], 2), round(loc['Y'], 2), round(loc['Z'], 2)],
                    'yaw': round(rot['Yaw'], 4)})
    return found


def build():
    objects = artifact_objects(json.loads(ENVOBJDATA.read_text(encoding='utf-8')))
    maps = {}
    for path in sorted(ALL_MAPDATA.glob('*/table-*.json')):
        if not path.stem.split('-')[-1].isdigit():
            continue
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        found = placements({'SpawnInfoList': data.get('SpawnInfoList', [])}, objects)
        if found:
            maps.setdefault(str(data['MapId']), []).extend(found)
    if not maps:   # only the Poeta probe extracted
        for map_id, path in MAPDATA.items():
            if path.exists():
                maps[str(map_id)] = placements(json.loads(path.read_text(encoding='utf-8')), objects)
    DATA.write_text(json.dumps({'source': 'MapData.dat SpawnInfoList + EnvObjData.dat TeleportArtifact usage',
                                'maps': maps}, separators=(',', ':')) + '\n', encoding='utf-8')
    return maps


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    maps = build()
    print({m: len(p) for m, p in maps.items()})


if __name__ == '__main__':
    main()
