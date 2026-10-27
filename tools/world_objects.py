"""Quest objects and trigger areas of every map, from the client's own data.

Sources:
- research/local/all-mapdata-full/*/table-*.json (MapData.dat of every map, as tools/map_points.py):
  SpawnInfoList records with an EnvObjIdList (unique key, positions, yaw) and TriggerActorDataMap
  (name, location, direction, Box/Sphere collision, box size, radius, height).
- EnvObjData.dat (research/local/world/gameplay-tables/table-000.json): usage, interact time, range
  and count, relationship (ERelationshipEntity; the official Kibelisk spawn carries 8 EnvObj_Light).
- tools/data/quests.json: which objects UseEnvObj objectives name, which triggers EnterVolumePC names.

Objects kept: every placement of an object a quest uses, of Quest-usage objects, and of gather nodes
(Usage GatherSource: UsageValue names a GatherSource row, whose MaterialGroup gives the material, count and bonus
item; research/local/craft-legion-20261016/tables table-010 GatherSource, table-011 GatherSourceMaterial). Teleport
artifacts stay in tools/teleport_artifacts.py. Triggers kept: Quest-function triggers and any a
quest names.

    python tools/world_objects.py build    # writes tools/data/world-objects.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MAPDATA = ROOT / 'research/local/all-mapdata-full'
ENVOBJDATA = ROOT / 'research/local/world/gameplay-tables/table-000.json'
QUESTS = ROOT / 'tools/data/quests.json'
GATHER_TABLES = ROOT / 'research/local/craft-legion-20261016/tables'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
DATA = ROOT / 'tools/data/world-objects.json'

RELATIONSHIP = {'NoRelation': 0, 'EnvObj_Neutral': 7, 'EnvObj_Light': 8, 'EnvObj_Dark': 9, 'Rift': 10, 'EnvObj_Destroy': 15,
                'EnvObj_RoleA': 26, 'EnvObj_RoleB': 27, 'EnvObj_MonsterEnemy': 28}


def walk(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from walk(value)
    elif isinstance(node, list):
        for value in node:
            yield from walk(value)


def envobj_rows(table):
    """EnvObjData id -> the fields the server uses."""
    rows = {}
    for row in walk(table):
        usage = row.get('Usage')
        if isinstance(usage, str) and usage.startswith('EEnvObjectUsage::') and isinstance(row.get('ID'), dict):
            relation = str(row.get('RelationshipEntity', '')).split('::')[-1]
            rows[row['ID']['Value']] = {
                'name': row.get('Name'), 'usage': usage.split('::')[-1],
                'interact_time': int(row.get('InteractTime') or 0), 'interact_range': int(row.get('InteractRange') or 0),
                'interact_count': int(row.get('InteractCount') or 0), 'despawn': bool(row.get('bDespawn')),
                'usage_value': row.get('UsageValue') if isinstance(row.get('UsageValue'), str) else None,
                'relationship': RELATIONSHIP.get(relation, 7),
                'long_range': str(row.get('VisibleRangeType', '')).endswith(('LongRange', 'MapRange'))}
    return rows


def quest_targets(quests):
    """(object ids UseEnvObj names, trigger names EnterVolumePC names)."""
    envs, triggers = set(), set()
    for quest in quests['quests'].values():
        for step in quest['steps']:
            for goal in step['objectives']:
                if goal['type'] == 'UseEnvObj':
                    envs.update(goal.get('envs') or [])
                elif goal['type'] == 'EnterVolumePC' and goal.get('target'):
                    triggers.add(goal['target'])
    return envs, triggers


def gather_sources(tables=GATHER_TABLES, catalog=CATALOG):
    """GatherSource name -> [material item, count, bonus item or 0, bonus count, mastery level, gather gauge]."""
    if not (tables / 'table-010.json').exists():
        return {}
    ids = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    rows = lambda n: json.loads((tables / n).read_text(encoding='utf-8'))['Properties']['Data']
    groups = {}
    for row in rows('table-011.json'):
        if row.get('MaterialName') in ids:
            extra = next(((ids[a['Item']], int(a.get('Count') or 1)) for a in row.get('AddMaterialList') or [] if a.get('Item') in ids), (0, 0))
            groups.setdefault(row['MaterialGroup'], [ids[row['MaterialName']], int(row.get('MaterialCount') or 1), extra[0], extra[1], int(row.get('GatherGauge') or 360)])
    return {row['Name']: groups[row['MaterialGroup']][:4] + [int(row.get('MasteryLevel') or 1), groups[row['MaterialGroup']][4]]
            for row in rows('table-010.json') if row.get('MaterialGroup') in groups}


def party_portals(rows, dungeons):
    """Portal EnvObj id -> the dungeons it enters. EnterDungeon / EnterInstanceLayer objects name one dungeon in
    UsageValue (digits, or a single character whose code is the id). PartyDungeon portals name their expedition by
    name family (E_PD_KraoCave_EnterPortal_001 -> KraoCave_Easy/Normal/Hard; two portals are misspelt)."""
    fixes = {'urugugucaynon': 'urugugucanyon', 'fiercehorncave': 'horncave'}
    order = {'easy': 0, 'normal': 1, 'hard': 2}
    out = {}
    for env, row in rows.items():
        if row['usage'] in ('EnterDungeon', 'EnterInstanceLayer'):
            value = row.get('usage_value') or ''
            dungeon = int(value) if value.isdigit() else ord(value) if len(value) == 1 else 0
            if dungeon and str(dungeon) in dungeons:
                out[env] = [dungeon]
            continue
        if row['usage'] != 'PartyDungeon' or not str(row['name']).startswith('E_PD_'):
            continue
        family = str(row['name'])[5:].split('_EnterPortal')[0].replace('_', '').lower()
        family = fixes.get(family, family)
        found = []
        for dungeon_id, room in dungeons.items():
            name = room['name'].replace('_', '').lower()
            if room['type'] == 'Party' and name.startswith(family) and name[len(family):] in order:
                found.append((order[name[len(family):]], int(dungeon_id)))
        if found:
            out[env] = [d for _, d in sorted(found)]
    return out


def objects(data, rows, wanted, gathers=None, everything=False, portals=None):
    """Placed EnvObjs: quest objects, gather nodes and the wanted ids; <paramref>everything</paramref> keeps every placed
    object with a row (instance maps: map events name walls, wagons and doors by spawn name)."""
    gathers = gathers or {}
    portals = portals or {}
    found = []
    for record in data.get('SpawnInfoList', []):
        for entry in record.get('EnvObjIdList') or []:
            env = entry.get('Value') if isinstance(entry, dict) else None
            row = rows.get(env)
            gather = gathers.get(row['usage_value']) if row is not None and row['usage'] == 'GatherSource' else None
            portal = portals.get(env)
            if row is None or row['usage'] == 'TeleportArtifact' or not (everything or env in wanted or row['usage'] == 'Quest' or gather or portal):
                continue
            for position in record.get('Positions') or []:
                loc, rot = position['Location'], position['Rotation']
                found.append({'env': env, 'name': record.get('Name'), 'unique_key': record.get('UniqueKey', 0),
                              'position': [round(loc['X'], 1), round(loc['Y'], 1), round(loc['Z'], 1)], 'yaw': round(rot['Yaw'], 3),
                              'interact_time': row['interact_time'], 'interact_range': row['interact_range'],
                              'relationship': row['relationship']})
                if gather:
                    # [..., gather gauge, attempts]: EnvObjData InteractCount is the node's attempts (1-3; guides: three tries).
                    found[-1]['gather'] = gather + [row['interact_count'] or 3]
                if portal:
                    found[-1]['portal'] = portal
                if row['usage'] == 'OdEnergyCube':
                    found[-1]['cube'] = True
                # Long-range objects (EnvObjData VisibleRangeType LongRange/MapRange): an instance map sends them at entry
                # (OBSERVED: the Ascension Subspace's doors and flight rings, all at once, up to 400 m away).
                if row.get('long_range'):
                    found[-1]['long_range'] = True
                # Flight rings (Usage FlightRing): flying through refills the flight points (OBSERVED 2026-10-19).
                if row['usage'] == 'FlightRing':
                    found[-1]['flight_ring'] = True
    return found


def triggers(data, wanted, map_events=False):
    """Quest trigger areas and the wanted names; <paramref>map_events</paramref> adds the MapEvent triggers (EnterVolumePC objectives)."""
    kinds = ('ETriggerActorFuncType::Quest', 'ETriggerActorFuncType::MapEvent') if map_events else ('ETriggerActorFuncType::Quest',)
    found = []
    for entry in data.get('TriggerActorDataMap', []):
        value = entry['Value']
        name = value.get('Name')
        if not name or not (name in wanted or value.get('FuncType') in kinds):
            continue
        loc, box = value['Location'], value.get('BoxSize') or {}
        # id (i64) and env (EnvObjData E_Trigger / _air / _water): the trigger actor the server spawns (0x3634 type 1), which
        # the client marks with the objective beam and reports entering (OBSERVED: every subspace trigger, 2026-10-19).
        env = (value.get('EnvObjId') or {}).get('Value', 0)
        found.append({'name': name, 'id': (value.get('ID') or {}).get('Value', 0), 'env': env,
                      'position': [round(loc['X'], 1), round(loc['Y'], 1), round(loc['Z'], 1)],
                      'direction': round(value.get('Direction', 0.0), 5),
                      'shape': str(value.get('CollisionType', '')).split('::')[-1],
                      'box': [round(box.get('X', 0.0), 1), round(box.get('Y', 0.0), 1)],
                      'radius': round(value.get('Radius', 0.0), 1), 'height': round(value.get('Height', 0.0), 1)})
    return found


def served_instance_maps(path=ROOT / 'tools/data/map-events.json'):
    """The instance layers and dungeons with map events (tools/map_events.py): all their objects and triggers are kept."""
    try:
        return tuple(json.loads(Path(path).read_text(encoding='utf-8')).get('served_maps') or ())
    except (OSError, ValueError):
        return ()


def build():
    rows = envobj_rows(json.loads(ENVOBJDATA.read_text(encoding='utf-8')))
    envs, trigger_names = quest_targets(json.loads(QUESTS.read_text(encoding='utf-8')))
    gathers = gather_sources()
    instance_maps = set(served_instance_maps())
    try:
        dungeon_rows = json.loads((ROOT / 'tools/data/map-events.json').read_text(encoding='utf-8')).get('dungeons') or {}
    except (OSError, ValueError):
        dungeon_rows = {}
    portals = party_portals(rows, dungeon_rows)
    maps = {}
    for path in sorted(MAPDATA.glob('*/table-*.json')):
        if not path.stem.split('-')[-1].isdigit():
            continue
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        instance = data.get('MapId') in instance_maps
        placed, areas = objects(data, rows, envs, gathers, everything=instance, portals=portals), triggers(data, trigger_names, map_events=instance)
        if placed or areas:
            entry = maps.setdefault(str(data['MapId']), {'objects': [], 'triggers': []})
            entry['objects'].extend(placed)
            entry['triggers'].extend(areas)
    return {'source': 'MapData.dat SpawnInfoList/TriggerActorDataMap + EnvObjData.dat', 'maps': maps}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    out = build()
    DATA.write_text(json.dumps(out, separators=(',', ':')) + '\n', encoding='utf-8')
    maps = out['maps']
    print(len(maps), 'maps,', sum(len(m['objects']) for m in maps.values()), 'objects,',
          sum(len(m['triggers']) for m in maps.values()), 'triggers ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
