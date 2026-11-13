"""Quest objects and trigger areas of every map, from the client's own data.

Sources:
- research/local/all-mapdata-full/*/table-*.json (MapData.dat of every map, as tools/map_points.py):
  SpawnInfoList records with an EnvObjIdList (unique key, positions, yaw) and TriggerActorDataMap
  (name, location, direction, Box/Sphere collision, box size, radius, height).
- EnvObjData.dat (research/local/world/gameplay-tables/table-000.json): usage, interact time, range
  and count, relationship (ERelationshipEntity; the official Kibelisk spawn carries 8 EnvObj_Light).
- tools/data/quests.json: which objects UseEnvObj objectives name, which triggers EnterVolumePC names.

Objects kept: every placement of an object a quest uses, of Quest-usage objects, of the Empyrean Traces and Monoliths
(tools/data/monoliths.json; the traces are Usage Normal objects, so they were left out until 2026-11-11 although the client
places ~560 per field map; each trace carries its spawn's UseEnvObjSpawnerName achievement, OBSERVED in its 0x3634), and of gather nodes
(Usage GatherSource: UsageValue names a GatherSource row, whose MaterialGroup gives the material, count and bonus
item; research/local/craft-legion-20261016/tables table-010 GatherSource, table-011 GatherSourceMaterial). Teleport
artifacts stay in tools/teleport_artifacts.py. Triggers kept: Quest-function triggers and any a
quest names.

    python tools/world_objects.py build    # writes tools/data/world-objects.json
    python tools/world_objects.py traces [--tables DIR]   # appends the Empyrean Traces and Monoliths of the client table
                                                          # export (Map_*_MapData.json) to each map's list (indexes kept)
    python tools/world_objects.py rings [--tables DIR]    # appends the flight rings (Usage FlightRing) the list lacks: the
                                                          # open-world Abyss rings (Reshanta 20/23 E_AR1, map 80 E_AB1)
    python tools/world_objects.py decor [--tables DIR]    # appends the placed objects the list lacks that the official server
                                                          # spawns: decorations (Usage None/Prop), map-event props (MapEvent),
                                                          # landing sensors (the pianos), Normal objects (field-quest memorials and
                                                          # tombstones) and Quest objects, every placement of the MapData
    python tools/world_objects.py pools [--tables DIR]    # writes tools/data/world-pools.json: the random pools (hidden cubes and the
                                                          # Altgard treasure boxes) and the Spacetime Rift portals with their schedule,
                                                          # RiftData, exit points and the cube rewards. A separate file appended after
                                                          # world-objects.json at load (WorldObjects.cs), so every key of that file stays
                                                          # as it is and the two one-line files never collide in a merge.
"""
import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from map_events import SERVED_TYPES

ROOT = Path(__file__).resolve().parents[1]
MAPDATA = ROOT / 'research/local/all-mapdata-full'
ENVOBJDATA = ROOT / 'research/local/world/gameplay-tables/table-000.json'
QUESTS = ROOT / 'tools/data/quests.json'
GATHER_TABLES = ROOT / 'research/local/craft-legion-20261016/tables'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
REWARD_TABLES = ROOT / 'research/local/pet-tables-20261016'   # RewardItem table-001, RewardPackage table-003 (tools/loot_data.py)
DATA = ROOT / 'tools/data/world-objects.json'
CHEST_TABLE = re.compile(r'^reward_manual_FieldEvent_.+_TreasureBox_(\d+)$')
MONOLITHS = ROOT / 'tools/data/monoliths.json'
ACHIEVEMENTS = ROOT / 'tools/data/achievements.json'
TABLES = ROOT / 'research/local/tables-20261109-023531'
POOLS = ROOT / 'tools/data/world-pools.json'
MAP_POINTS = ROOT / 'tools/data/map-points.json'

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
                'reward_once': bool(row.get('bGetRewardOnce')),
                'relationship': RELATIONSHIP.get(relation, 7),
                'long_range': str(row.get('VisibleRangeType', '')).endswith(('LongRange', 'MapRange')),
                'hit_limit': int(row.get('HitLimit') or 0),
                'hit_skills': [s['Value'] for s in row.get('HitSkillIdList') or [] if isinstance(s, dict) and s.get('Value')],
                'finish_duration': int(row.get('FinishDuration') or 0),
                'not_have_abnormal_group': not_have_abnormal_group(row.get('InteractConditionList')),
                'combat_power': int(row.get('CombatPower') or 0),
                'life_time': int(row.get('LifeTime') or 0),
                'collision_radius': int(row.get('CollisionCylinderRadius') or 0),
                'level': int(row.get('Level') or 0), 'item_level': int(row.get('ItemLevel') or 0)}
    return rows


def not_have_abnormal_group(conditions):
    """The first abnormal group an InteractConditionList NotHaveAbnormalGroup row names (the BombBox: 1196), or 0.

    The rows are {ConditionType: EEnvObjCanInteractType::NotHaveAbnormalGroup, ConditionValues: ['1196']} (OBSERVED in
    EnvObjData.dat; A2Core PR #4's reader looked for a numeric field and never found one in this layout)."""
    for condition in conditions or []:
        if str(condition.get('ConditionType', '')).split('::')[-1] != 'NotHaveAbnormalGroup':
            continue
        for value in condition.get('ConditionValues') or []:
            first = str(value).split(',')[0].strip()
            if first.isdigit():
                return int(first)
    return 0


def trace_data(monoliths=MONOLITHS, achievements=ACHIEVEMENTS):
    """(trace EnvObjData ids, Monolith object id -> [monolith ids], spawn name (lower) -> UseEnvObjSpawnerName achievement)."""
    try:
        data = json.loads(Path(monoliths).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return set(), {}, {}
    spawner = {}
    try:
        for key, entry in json.loads(Path(achievements).read_text(encoding='utf-8'))['achievements'].items():
            if entry.get('t') == 'UseEnvObjSpawnerName' and entry.get('v'):
                spawner.setdefault(entry['v'][0].lower(), int(key))
    except (OSError, ValueError, KeyError):
        pass
    return {int(k) for k in data.get('traces') or {}}, {int(k): v for k, v in (data.get('objects') or {}).items()}, spawner


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


def chest_tables(tables=REWARD_TABLES):
    """EnvObjData id -> its field-event treasure box's reward table, by name: reward_manual_FieldEvent_<x>_TreasureBox_<id>
    (RewardItem / RewardPackage names). CANDIDATE: the naming convention; RewardData.dat rows with the same id name the same
    table for E_L1_FE_TH_S2_* and E_L1_FE_TB_* (1100311/12, 1200018-21), but _1200054/_1200055 are named by RewardData rows
    1201054/1201055 (GoldBox_002/003), their suffixes being Kibelisk ids (never placed here)."""
    out = {}
    for name in ('table-001.json', 'table-003.json'):
        path = Path(tables) / name
        if not path.exists():
            continue
        for row in json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']:
            match = CHEST_TABLE.match(str(row.get('Name') or ''))
            if match:
                out[int(match.group(1))] = row['Name']
    return out


def door_layer(name, dungeons, served_types=SERVED_TYPES):
    """The served instance layer a door is named after (World_L_A_Layer_01_EnterDoor -> World_L_A_Layer_01), or 0."""
    stem = str(name or '')
    for suffix in ('_EnterDoor', '_EnterDoor_IN'):
        if stem.endswith(suffix):
            stem = stem[:-len(suffix)]
            break
    else:
        return 0
    found = [int(i) for i, room in dungeons.items() if room['name'] == stem and room['type'] in served_types]
    return found[0] if len(found) == 1 else 0


def party_portals(rows, dungeons, served_types=SERVED_TYPES):
    """Portal EnvObj id -> the dungeons it enters. EnterDungeon / EnterInstanceLayer objects name one dungeon in
    UsageValue (digits, or a single character whose code is the id). The target must be a dungeon the server serves:
    World_L_A_Layer_01_EnterDoor (the Dawn Legion barracks door, HQ1102010 step 2) names 23, which Dungeon.dat has as the
    Abyss_Reshanta_C field map; the door enters the layer its own name gives (id 2). (A2Core PR #4.)
    PartyDungeon portals name their expedition by name family
    (E_PD_KraoCave_EnterPortal_001 -> KraoCave_Easy/Normal/Hard; two portals are misspelt)."""
    fixes = {'urugugucaynon': 'urugugucanyon', 'fiercehorncave': 'horncave'}
    order = {'easy': 0, 'normal': 1, 'hard': 2}
    out = {}
    for env, row in rows.items():
        if row['usage'] in ('EnterDungeon', 'EnterInstanceLayer'):
            value = row.get('usage_value') or ''
            dungeon = int(value) if value.isdigit() else ord(value) if len(value) == 1 else 0
            # A door named after its layer (World_L_A_Layer_01_EnterDoor) enters that layer: its UsageValue read as 23 (Abyss
            # Reshanta C) for dungeon 2 (live 2026-11-07, HQ1102010 step 2: the office door did nothing).
            named = [int(k) for k, d in dungeons.items() if str(row['name']).startswith(str(d.get('name')) + '_')]
            if named:
                dungeon = named[0]
            target = dungeons.get(str(dungeon))
            if target is None or target['type'] not in served_types:
                dungeon = door_layer(row.get('name'), dungeons, served_types)
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


def misleading(spawn, own, env_names):
    """A spawn entry whose name is another EnvObjData object's name (exactly or with a _n suffix)."""
    spawn, own = spawn.lower(), own.lower()
    if not own or spawn == own or spawn.startswith(own + '_'):
        return False
    parts = spawn.split('_')
    return any('_'.join(parts[:i]) in env_names for i in range(len(parts), 1, -1))


def objects(data, rows, wanted, gathers=None, everything=False, portals=None, dungeons=None, chests=None, traces=None, monoliths=None, spawner=None):
    """Placed EnvObjs: quest objects, gather nodes, Empyrean Traces, Monoliths and the wanted ids; <paramref>everything</paramref>
    keeps every placed object with a row (instance maps: map events name walls, wagons and doors by spawn name)."""
    gathers = gathers or {}
    portals = portals or {}
    chests = chests or {}
    env_names = {(row['name'] or '').lower() for row in rows.values()}
    traces = traces or set()
    monoliths = monoliths or {}
    spawner = spawner or {}
    found = []
    for record in data.get('SpawnInfoList', []):
        for entry in record.get('EnvObjIdList') or []:
            env = entry.get('Value') if isinstance(entry, dict) else None
            row = rows.get(env)
            gather = gathers.get(row['usage_value']) if row is not None and row['usage'] == 'GatherSource' else None
            portal = portals.get(env)
            # A door whose UsageValue names no served dungeon enters the layer its spawn entry is named after: the Dawn Legion
            # barracks door (EnvObjData E_L1_DawnLegionBaseBarracks_LayerEnterDoor_001, UsageValue 23 = the Abyss_Reshanta_C
            # field map) is spawned as World_L_A_Layer_01_EnterDoor -> layer 2 (HQ1102010 step 2).
            if row is not None and portal is None and row['usage'] in ('EnterDungeon', 'EnterInstanceLayer') and dungeons:
                portal = [layer] if (layer := door_layer(record.get('Name'), dungeons)) else None
            trace = env in traces
            if row is None or row['usage'] == 'TeleportArtifact' or not (everything or env in wanted or row['usage'] == 'Quest' or gather or portal
                                                                         or trace or env in monoliths):
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
                # An Empyrean Trace: its spawn's UseEnvObjSpawnerName achievement (OBSERVED: the 0x3634 of every recorded trace
                # carries it, e.g. E_D1_GodFragment_01_Ground_129 -> 125010210); it also marks the trace collected for good.
                if trace and spawner.get(str(record.get('Name')).lower()):
                    found[-1]['achievement'] = spawner[str(record.get('Name')).lower()]
                # A Monolith (Usage Monolith): the Monoliths it resonates with (Reshanta: both races').
                if env in monoliths:
                    found[-1]['monolith'] = monoliths[env]
                # Long-range objects (EnvObjData VisibleRangeType LongRange/MapRange): an instance map sends them at entry
                # (OBSERVED: the Ascension Subspace's doors and flight rings, all at once, up to 400 m away).
                if row.get('long_range'):
                    found[-1]['long_range'] = True
                # Flight rings (Usage FlightRing): flying through refills the flight points (OBSERVED 2026-10-19).
                if row['usage'] == 'FlightRing':
                    found[-1]['flight_ring'] = True
                # Exit doors (Usage MoveMap): UsageValue names the MapData teleport point the door leads to. (A2Core PR #4.)
                if row['usage'] == 'MoveMap' and row.get('usage_value'):
                    found[-1]['move_to'] = row['usage_value']
                # Destructible objects (RelationshipEntity EnvObj_Destroy): EnvObjData HitLimit hits break one (1 when unset),
                # HitSkillIdList limits the skills that count. (A2Core PR #4.)
                if row['relationship'] == RELATIONSHIP['EnvObj_Destroy']:
                    if row.get('hit_limit', 0) > 1:
                        found[-1]['hit_limit'] = row['hit_limit']
                    if row.get('hit_skills'):
                        found[-1]['hit_skills'] = row['hit_skills']
                # Carried-item boxes (BombBox: InteractConditionList NotHaveAbnormalGroup 1196): refused while the player has
                # that abnormal group; bDespawn hides the box for FinishDuration ms after its use. (A2Core PR #4.)
                if row.get('not_have_abnormal_group'):
                    found[-1]['carry'] = {'group': row['not_have_abnormal_group'], 'despawn': row['despawn'],
                                          'finish_duration': row['finish_duration']}
                # EnvObjData FinishDuration (ms) of an object that dies after use (bDespawn): it times EnvObjDie and the despawn
                # (OBSERVED in our recordings: EnvObjDie 50 ms after the use with none, about 650 / 1150 ms with 500 / 1000).
                if row['despawn'] and row['finish_duration']:
                    found[-1]['finish_ms'] = row['finish_duration']
                # Field-event treasure boxes: the reward table opening one pays (chest_tables; CANDIDATE link by name).
                if env in chests:
                    found[-1]['loot'] = chests[env]
                # Recovery orbs and their boxes (EnvObjData name *RecoveryOrb*; OBSERVED in EnvObjData.dat, build 26262663):
                # LifeTime (ms, 0 = unset), CollisionCylinderRadius (cm) and HitSkillIdList. Data only: the pickup effect is unknown.
                if 'RecoveryOrb' in str(row.get('name') or ''):
                    found[-1]['recovery_orb'] = {'life_time': row['life_time'], 'radius': row['collision_radius'],
                                                 'hit_skills': row.get('hit_skills') or []}
                # Gated objects (EnvObjData CombatPower, with its Level and ItemLevel: the Morheim / Eltnen portals).
                if row.get('combat_power'):
                    found[-1]['required_combat_power'] = row['combat_power']
                    if row.get('level'):
                        found[-1]['min_level'] = row['level']
                    if row.get('item_level'):
                        found[-1]['item_level'] = row['item_level']
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


def recovery_orbs(rows):
    """Every EnvObjData recovery orb or orb box (name *RecoveryOrb*), placed or not: id -> name, LifeTime (ms), collision
    radius (cm) and HitSkillIdList. The timed orbs (LifeTime 30000, hit skills 1801510/1801520/1801531) are not placed by
    any MapData; something spawns them at run time (unknown what). OBSERVED in EnvObjData.dat; data only."""
    return {str(env): {'name': row['name'], 'life_time': row['life_time'], 'radius': row['collision_radius'],
                       'hit_skills': row.get('hit_skills') or []}
            for env, row in sorted(rows.items()) if 'RecoveryOrb' in str(row.get('name') or '')}


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
    chests = chest_tables()
    traces, monoliths, spawner = trace_data()
    maps = {}
    for path in sorted(MAPDATA.glob('*/table-*.json')):
        if not path.stem.split('-')[-1].isdigit():
            continue
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        instance = data.get('MapId') in instance_maps
        placed = objects(data, rows, envs, gathers, everything=instance, portals=portals, dungeons=dungeon_rows, chests=chests,
                         traces=traces, monoliths=monoliths, spawner=spawner)
        areas = triggers(data, trigger_names, map_events=instance)
        if placed or areas:
            entry = maps.setdefault(str(data['MapId']), {'objects': [], 'triggers': []})
            entry['objects'].extend(placed)
            entry['triggers'].extend(areas)
    # Map events' UseEnvObj goals name an object by its spawn entry or its EnvObjData name (the server matches either exactly):
    # every placed object's EnvObjData name, by id.
    placed = {o['env'] for m in maps.values() for o in m['objects']}
    env_names = {str(env): rows[env]['name'] for env in sorted(placed) if rows[env]['name']}
    return {'source': 'MapData.dat SpawnInfoList/TriggerActorDataMap + EnvObjData.dat', 'maps': maps,
            'env_names': env_names, 'recovery_orbs': recovery_orbs(rows)}


def add_traces(current, tables=TABLES):
    """
    The Empyrean Traces and Monoliths of every map of the client table export (Table_EnvObjData.json, Map_*_MapData.json)
    appended to <paramref>current</paramref> (a world-objects.json) so the objects already there keep their index (entity key
    910000 + index). Run again, it first drops the traces and Monoliths it added. Returns map id -> objects added.
    """
    tables = Path(tables)
    rows = envobj_rows(json.loads((tables / 'Table_EnvObjData.json').read_text(encoding='utf-8')))
    traces, monoliths, spawner = trace_data()
    added = {}
    for path in sorted(tables.glob('Map_*_MapData.json')):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        placed = [o for o in objects(data, rows, set(), traces=traces, monoliths=monoliths, spawner=spawner)
                  if o['env'] in traces or o['env'] in monoliths]
        if not placed:
            continue
        entry = current['maps'].setdefault(str(data['MapId']), {'objects': [], 'triggers': []})
        entry['objects'] = [o for o in entry['objects'] if o['env'] not in traces and o['env'] not in monoliths]
        seen = set()
        for o in placed:
            if (o['env'], o['unique_key'], tuple(o['position'])) not in seen:
                seen.add((o['env'], o['unique_key'], tuple(o['position'])))
                entry['objects'].append(o)
        added[str(data['MapId'])] = added.get(str(data['MapId']), 0) + len(seen)
    return added


def add_rings(current, tables=TABLES):
    """
    The flight rings (EnvObjData Usage FlightRing) of every map of the client table export that <paramref>current</paramref> (a
    world-objects.json) lacks, appended so the objects already there keep their index. Instance maps already list theirs (every
    placed object); the open-world ones (the Abyss: Reshanta A/D E_AR1_FlyCircle_001, map 80 E_AB1_FlyCircle_001) were missing, so
    the rings did not exist there. Placements already listed (env, unique key, position) are kept. Returns map id -> rings added.
    """
    tables = Path(tables)
    rows = envobj_rows(json.loads((tables / 'Table_EnvObjData.json').read_text(encoding='utf-8')))
    ring_envs = {env for env, row in rows.items() if row['usage'] == 'FlightRing'}
    added = {}
    for path in sorted(tables.glob('Map_*_MapData.json')):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        placed = [o for o in objects(data, rows, ring_envs) if o['env'] in ring_envs]
        if not placed:
            continue
        entry = current['maps'].setdefault(str(data['MapId']), {'objects': [], 'triggers': []})
        seen = {(o['env'], o['unique_key'], tuple(o['position'])) for o in entry['objects']}
        count = 0
        for o in placed:
            if (o['env'], o['unique_key'], tuple(o['position'])) not in seen:
                seen.add((o['env'], o['unique_key'], tuple(o['position'])))
                entry['objects'].append(o)
                count += 1
        if count:
            added[str(data['MapId'])] = added.get(str(data['MapId']), 0) + count
    return added


# Usages the decor command adds (OBSERVED: every field-map EnvObjSpawn_NT of the official recordings 2026-11-06/07 that our list lacked
# was one of these: the Drakan base's E_L0_Region_07_TowerDeco_001 / BombBoxDeco_001, E_L0_Region_04_WoodWall_001 on map 1000; dead
# villagers, bonfires, carts, honeycombs, the pianos E_Piano_* (LandingSensor), chairs (Prop) on map 1110; all placements of a
# Multi_ spawn record appear, e.g. the 9 of EnvObj_L0_R7_Multi_BombBoxDeco_1).
DECOR_USAGES = ('None', 'Prop', 'MapEvent', 'LandingSensor', 'Normal', 'Quest')
# Random pools: thousands of placements of which the official server shows a few (3 of 2720 E_D1_TreasureBoxRare_002 seen); their
# selection rule is not known, so they stay out (CANDIDATE).
DECOR_POOLS = ('HiddenCube', 'TreasureBox')


def add_decor(current, tables=TABLES):
    """
    The placed objects of every map of the client table export (Usage in DECOR_USAGES, random pools left out) that <paramref>current</paramref>
    (a world-objects.json) lacks, appended so the objects already there keep their index (entity key 910000 + index). Placements
    already listed (env, unique key, position) are kept; run again it adds nothing. Returns map id -> objects added.
    """
    tables = Path(tables)
    rows = envobj_rows(json.loads((tables / 'Table_EnvObjData.json').read_text(encoding='utf-8')))
    wanted = {env for env, row in rows.items() if row['usage'] in DECOR_USAGES and not any(p in str(row['name']) for p in DECOR_POOLS)}
    added = {}
    for path in sorted(tables.glob('Map_*_MapData.json')):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        placed = [o for o in objects(data, rows, wanted) if o['env'] in wanted]
        if not placed:
            continue
        entry = current['maps'].setdefault(str(data['MapId']), {'objects': [], 'triggers': []})
        seen = {(o['env'], o['unique_key'], tuple(o['position'])) for o in entry['objects']}
        count = 0
        for o in placed:
            if (o['env'], o['unique_key'], tuple(o['position'])) not in seen:
                seen.add((o['env'], o['unique_key'], tuple(o['position'])))
                entry['objects'].append(o)
                count += 1
        if count:
            added[str(data['MapId'])] = added.get(str(data['MapId']), 0) + count
    return added


# ---------- Random pools and Spacetime Rifts (world-pools.json) ----------

# The four field maps that hold the pools and the rifts (MapData World_L_A/L_B/D_A/D_B; Light 1010/1011, Dark 1110/1111).
FIELD_MAPS = (1010, 1011, 1110, 1111)
FIELD_SHORT = {1010: 'L1', 1011: 'L2', 1110: 'D1', 1111: 'D2'}
WEEKDAYS = ('bSunday', 'bMonday', 'bTuesday', 'bWednesday', 'bThursday', 'bFriday', 'bSaturday')


def _table(tables, name):
    return json.loads((Path(tables) / name).read_text(encoding='utf-8'))['Properties']['Data']


def pools(tables=TABLES, map_points=MAP_POINTS):
    """
    The random pools and the Spacetime Rifts of the field maps, from the client table export:

    - Pools: every MapData SpawnInfoList record whose EnvObjIdList names hidden cubes only (EnvObjData Category HiddenCubeLight /
      HiddenCubeDark: E_HiddenCube_*, E_L2/E_D2_HiddenCube_*, E_D1_TreasureBoxRare_002, E_D1_HiddenCube_*). Each lists 2 ids and
      2-43 positions. Every one of the 206 has fewer ids than positions, as every other "one of several points" spawner of the maps
      (EnvObj_D1_RiftPortal_1: 1 id, 5 positions; the period field boss: 1 NPC, 6 points), while the 1957 fixed objects of map 1110
      have one id per position. Read as: the record keeps len(EnvObjIdList) objects up at once, slot i of kind EnvObjIdList[i], each
      at a random free position of the record (CANDIDATE reading; the 3 official cube spawns of 2026-11-06 each stood on a position
      of its record). One record has as many ids as positions (E_L2_LankusisPlateau_HiddenCube_Water: both stand, each comes back after
      it is opened). One entry per (position, kind); entry.pool = the record's UniqueKey, entry.pool_slots = its EnvObjIdList.
    - Rifts: the EnvObjData Usage Rift objects a PeriodSpawn row places (Cycle, 02:00 KST every 180 min, all week; one of the
      record's 5 positions): entry.rift = the spawner name. rifts[spawner] = the RiftData row of its UsageValue (ArrivalMapId,
      Min/MaxLevel, LifeTime s, CostList), the use limit (InteractCount), the use time (InteractTime), the teleport effect
      (UsageValue2 -> TeleportEffect.dat id), the PeriodSpawn row and the exit points: Teleport.dat RiftPortal_<from>_to_<to>_Teleport_<n>
      (n = the RiftData row's number) on the arrival map, its ActorName list resolved in map-points.json (MapData points).
    - cubes[env]: RewardData RewardName (no key) and BasicKeyRewards (with a key), bIsKeyOnly, the key items (RequireValues ->
      Item.dat ids). The L2/D2 cubes have no RewardData row: they take their faction's L/D tables (CANDIDATE).
    - tables: the key reward tables (RewardItem reward_manual_TreasureBox_L_Key / _D_Key) in loot.json's form, which loot.json lacks
      (tools/loot_data.py keeps RewardName tables only); their Light / Dark packages are in box-race.json.
    """
    tables = Path(tables)
    env_rows = {r['ID']['Value']: r for r in _table(tables, 'Table_EnvObjData.json')}
    rows = envobj_rows({'Data': list(env_rows.values())})
    cube_ids = {i for i, r in env_rows.items() if str(r.get('Category', '')).split('::')[-1] in ('HiddenCubeLight', 'HiddenCubeDark')}
    rift_ids = {i for i, r in env_rows.items() if str(r.get('Usage', '')).endswith('::Rift')}
    item_ids = {r['Name']: r['ID']['Value'] for r in _table(tables, 'Table_Item.json') if isinstance(r.get('ID'), dict)}
    reward_data = {r['ID']['Value']: r for r in _table(tables, 'Table_RewardData.json')}
    rift_data = {r['Name']: r for r in _table(tables, 'Table_RiftData.json')}
    effects = {r['Name']: r['ID']['Value'] for r in _table(tables, 'Table_TeleportEffect.json')}
    teleports = {r['Name']: r for r in _table(tables, 'Table_Teleport.json')}
    periods = {r['SpawnerName']: r for r in _table(tables, 'Table_PeriodSpawn.json')}
    points = json.loads(Path(map_points).read_text(encoding='utf-8'))['maps']

    def entry(record, env, position):
        loc, rot = position['Location'], position['Rotation']
        row = rows[env]
        return {'env': env, 'name': record.get('Name'), 'unique_key': record.get('UniqueKey', 0),
                'position': [round(loc['X'], 1), round(loc['Y'], 1), round(loc['Z'], 1)], 'yaw': round(rot['Yaw'], 3),
                'interact_time': row['interact_time'], 'interact_range': row['interact_range'], 'relationship': row['relationship']}

    maps, rifts = {}, {}
    for path in sorted(tables.glob('Map_World_World_*_MapData.json')):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        map_id = data.get('MapId')
        if map_id not in FIELD_MAPS:
            continue
        placed = maps.setdefault(str(map_id), [])
        for record in data.get('SpawnInfoList', []):
            ids = [e.get('Value') for e in record.get('EnvObjIdList') or [] if isinstance(e, dict)]
            positions = record.get('Positions') or []
            if ids and all(i in cube_ids for i in ids) and len(ids) <= len(positions):
                for position in positions:
                    for env in dict.fromkeys(ids):
                        placed.append(entry(record, env, position) | {'pool': record.get('UniqueKey', 0), 'pool_slots': ids})
            elif len(ids) == 1 and ids[0] in rift_ids and record.get('Name') in periods:
                env, spawner = ids[0], record['Name']
                rift = rift_data.get(rows[env]['usage_value'] or '')
                period = periods[spawner]
                if rift is None:
                    continue
                arrival = rift['ArrivalMapId']['Value']
                number = rift['Name'].rsplit('_', 1)[-1].lstrip('0') or '1'   # Morheim_RiftPortal_02 -> Teleport_2
                teleport = teleports.get('RiftPortal_%s_to_%s_Teleport_%s' % (FIELD_SHORT[map_id], FIELD_SHORT.get(arrival, '?'), number))
                names = [n.strip() for n in (teleport or {}).get('ActorName', '').split(',') if n.strip()]
                exits = [points.get(str(arrival), {}).get(n) for n in names]
                for position in positions:
                    placed.append(entry(record, env, position) | {'rift': spawner})
                rifts[spawner] = {
                    'map': map_id, 'env': env, 'rift': rift['Name'], 'rift_id': rift['ID']['Value'], 'arrival_map': arrival,
                    'min_level': rift['MinLevel'], 'max_level': rift['MaxLevel'], 'life_s': rift['LifeTime'],
                    'costs': [[item_ids.get(c['Item'], 0), c['Count']] for c in rift.get('CostList') or []],
                    'limit': rows[env]['interact_count'], 'interact_ms': rows[env]['interact_time'],
                    'effect': effects.get(env_rows[env].get('UsageValue2') or '', 0),
                    'period': [period['ID']['Value'], period['SpawnType'].split('::')[-1], round(period['Time']['Ticks'] / 10_000_000),
                               period.get('Cycle') or 0, ''.join('1' if period.get(d) else '0' for d in WEEKDAYS)],
                    'teleport': (teleport or {}).get('Name'), 'exits': [e for e in exits if e]}
    cubes = {}
    for env in sorted(cube_ids):
        row = env_rows[env]
        light = str(row.get('Category', '')).endswith('HiddenCubeLight')
        reward = reward_data.get(env)
        normal = reward['RewardName'] if reward else ('reward_manual_TreasureBox_L_Normal' if light else 'reward_manual_TreasureBox_D_Normal')
        key = (reward or {}).get('BasicKeyRewards') or ['reward_manual_TreasureBox_L_Key' if light else 'reward_manual_TreasureBox_D_Key']
        cubes[str(env)] = {'name': row['Name'], 'faction': 1 if light else 2, 'key_only': bool(row.get('bIsKeyOnly')),
                           'optional_key': bool(row.get('bIsOptionalRequirement')), 'normal': normal, 'key': key[0],
                           'keys': [item_ids[n] for n in row.get('RequireValues') or [] if n in item_ids],
                           'key_count': int(row.get('RequireCount') or 1), 'reward_data': reward is not None}
    wanted = {c['key'] for c in cubes.values()}
    loot_tables = {}
    for r in _table(tables, 'Table_RewardItem.json'):
        if r['Name'] in wanted:
            items = [[item_ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1),
                      int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                     for i in r.get('Items') or [] if i.get('ItemName') in item_ids and int(i.get('ItemProb') or 0) > 0]
            loot_tables.setdefault(r['Name'], {'race': 0, 'items': [], 'packages': []})['items'].extend(items)
    return {'source': 'MapData SpawnInfoList (Map_World_World_*_MapData) + EnvObjData + RiftData + PeriodSpawn + Teleport + '
                      'TeleportEffect + RewardData/RewardItem + Item.dat (client tables 2026-11-09) + map-points.json '
                      '(tools/world_objects.py pools)',
            'maps': {k: v for k, v in maps.items() if v}, 'rifts': rifts, 'cubes': cubes, 'tables': loot_tables}

def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build', 'traces', 'rings', 'decor', 'pools'])
    parser.add_argument('--tables', default=str(TABLES))
    args = parser.parse_args()
    if args.command == 'traces':
        current = json.loads(DATA.read_text(encoding='utf-8'))
        added = add_traces(current, args.tables)
        DATA.write_text(json.dumps(current, separators=(',', ':')) + '\n', encoding='utf-8')
        for map_id, count in sorted(added.items(), key=lambda kv: int(kv[0])):
            print(map_id, count)
        print(sum(added.values()), 'traces and Monoliths on', len(added), 'maps ->', DATA.relative_to(ROOT))
        return
    if args.command == 'decor':
        current = json.loads(DATA.read_text(encoding='utf-8'))
        added = add_decor(current, args.tables)
        DATA.write_text(json.dumps(current, separators=(',', ':')) + chr(10), encoding='utf-8', newline=chr(10))
        for map_id, count in sorted(added.items(), key=lambda kv: int(kv[0])):
            print(map_id, count)
        print(sum(added.values()), 'placed objects on', len(added), 'maps ->', DATA.relative_to(ROOT))
        return
    if args.command == 'pools':
        out = pools(args.tables)
        POOLS.write_text(json.dumps(out, separators=(',', ':')) + chr(10), encoding='utf-8', newline=chr(10))
        for map_id, entries in sorted(out['maps'].items(), key=lambda kv: int(kv[0])):
            print(map_id, sum(1 for o in entries if 'pool' in o), 'pool points,', sum(1 for o in entries if 'rift' in o), 'rift points')
        print(len(out['rifts']), 'rifts,', len(out['cubes']), 'cube kinds ->', POOLS.relative_to(ROOT))
        return
    if args.command == 'rings':
        current = json.loads(DATA.read_text(encoding='utf-8'))
        added = add_rings(current, args.tables)
        DATA.write_text(json.dumps(current, separators=(',', ':')) + '\n', encoding='utf-8', newline='\n')
        for map_id, count in sorted(added.items(), key=lambda kv: int(kv[0])):
            print(map_id, count)
        print(sum(added.values()), 'flight rings on', len(added), 'maps ->', DATA.relative_to(ROOT))
        return
    out = build()
    DATA.write_text(json.dumps(out, separators=(',', ':')) + '\n', encoding='utf-8')
    maps = out['maps']
    print(len(maps), 'maps,', sum(len(m['objects']) for m in maps.values()), 'objects,',
          sum(len(m['triggers']) for m in maps.values()), 'triggers ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
