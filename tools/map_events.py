"""Map events: the scripted stages of instance layers and dungeons -> tools/data/map-events.json.

The client ships one definition per event in Aion2/Content/Data/MapEvent/<Name>_MapEvent.dat: the whole file
is JSON under the data files' 4-byte XOR mask (FAion2DatFileArchive in CUE4Parse; the raw files are saved by the
asset probe's ASSETPROBE_LIST mode into research/local/mapevent-all). Each event names its map, its start
conditions (which quest step must be active), and stages of steps: directives the server runs before a step
(spawns, NPC walks, captions, delays), objectives the player completes (kills, talks, objects, trigger areas),
directives after each objective and after the step. The vocabulary is EMapEventDirectiveType / EQuestObjectiveType /
EMapEventCondition from the usmap. See research/map-events.md.

Joined here:
- Dungeon.dat (research/local/dungeon-tables): dungeon id -> map, type, exit rule. NPCs whose NpcData FunctionType
  is EnterInstanceLayer carry the dungeon id in FuncValues[0]; base-map trigger actors of FuncType EnterInstanceLayer
  carry it as DungeonId.
- MapData of every event map (research/local/all-mapdata-full): teleport points (PCTeleport, MapExit, arrival), the
  instance-layer volume over the base map, and which spawn entries are started by a directive (those are not
  spawned at map load; OBSERVED in the HQ001 recording: Robin and the later Cherubim groups appeared on their
  StartSpawner, the others at entry).
- Teleport.dat (research/local/teleport-tables-20261019): PCTeleport names that are not a point of the event's own
  map (Dungeon_L_0016_Teleport_BossRoom, Dungeon_0005_StartPoint, ...) -> the target map and its MapData teleport
  actor's location (null for PlayerStart: the server uses the map's arrival point).
- TeleportEffect.dat (research/local/teleport-effect-20261019): the PCTeleport effect names (value 1: Teleport_NoEffect,
  TeleportEffect_Dungeon_0005, ...) -> row id, the teleport effect byte of PrepareEnterMap (0x3621).

Usage: python tools/map_events.py build
"""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RAW = ROOT / 'research/local/mapevent-all/files/AION2/Content/Data/MapEvent'
DUNGEONS = ROOT / 'research/local/dungeon-tables/table-000.json'
NPCDATA = ROOT / 'research/local/npc-table/table-000.json'
MAPDATA = ROOT / 'research/local/all-mapdata-full'
MAPDATA_INDEX = ROOT / 'research/local/instance-ai-20261016/mapdata-files.json'
TELEPORTS = ROOT / 'research/local/teleport-tables-20261019/table-000.json'
TELEPORT_EFFECTS = ROOT / 'research/local/teleport-effect-20261019/table-000.json'
# A dungeon's own arrival cutscene, played (Cutscene event function, no wait) right after the load before the map's events join.
# OBSERVED in the official recording of 2026-10-19 (the Ascension dungeon: the player lands on a knee by Ashar); no table names it.
ARRIVAL_CUTSCENES = {'Dungeon_0005': 'Cutscene_L_A_Seq_1293'}
DATA = ROOT / 'tools/data/map-events.json'
XOR_KEY = bytes([0x25, 0xa8, 0x7e, 0x91])
# Dungeon types the server serves now: story layers and quest dungeons. Party/seal/field content waits for matching.
# Party dungeons (the expeditions) joined 2026-10-18: entered through their PartyDungeon portal objects.
# Sealed dungeons, awakening dungeons, the daily and boss-challenge dungeons joined later the same day: solo copies
# entered through their EnterDungeon portals (EnvObjData usage value = dungeon id).
# Field events (the Shugo festival mini-games and invasions) followed: entered through their EnterFieldEvent NPCs, a
# ready phase (FieldEvent.dat ReadyDuration) then the event (ProgressTime).
# Matching (the arenas) last: their maps' points and scripts serve the arena matches (GameSession.Arena.cs).
# Raid (four 10-player dungeons, entered through the client's dungeon-room window, unrecorded: staff enter with EnterDungeon).
# AbyssArtifact (12 artifact dungeons off the Abyss, portals on map 20), Growth, Guild and PersonalAgit (the legion halls)
# have no scripts: their maps are served for their points and spawns alone.
SERVED_TYPES = ('InstanceLayer', 'Quest', 'Ascension', 'Party', 'Seal', 'Awaken', 'Daily', 'BossChallenge', 'FieldEvent', 'Matching', 'Raid',
                'AbyssArtifact', 'Growth', 'Guild', 'PersonalAgit')
# Spawner directives: the named spawn entries wait for their directive instead of spawning at map load.
SPAWNER_DIRECTIVES = ('StartSpawner',)


def decode(raw: bytes) -> dict:
    """One .dat -> its JSON (the file is JSON under the data files' XOR mask)."""
    plain = bytes(b ^ XOR_KEY[i % 4] for i, b in enumerate(raw))
    return json.loads(plain.decode('utf-8-sig'))


def _enum(value):
    return str(value or '').split('::')[-1]


def _values(node):
    return [v.get('value') if isinstance(v, dict) else v for v in node.get('value_list') or []]


def _conditions(groups):
    """[[{Type, Value}]] (groups OR, members AND; CANDIDATE) -> [[{t, v}]]."""
    out = []
    for group in groups or []:
        if isinstance(group, dict):
            group = [group]
        if not isinstance(group, list):
            continue
        members = [{'t': c['Type'], 'v': list(c.get('Value') or [])} for c in group if isinstance(c, dict) and 'Type' in c]
        if members:
            out.append(members)
    return out


def _directive(node):
    out = {'id': node.get('Id', 0), 't': node['Type'], 'v': _values(node)}
    if node.get('LoopCount', 1) != 1:
        out['n'] = node['LoopCount']
    if node.get('LoopInterval'):
        out['i'] = node['LoopInterval']
    if node.get('LoopPreDelay'):
        out['pre'] = True
    conditions = _conditions(node.get('Conditions'))
    if conditions:
        out['c'] = conditions
    return out


def _directives(nodes):
    return [_directive(d) for d in nodes or [] if isinstance(d, dict) and d.get('Type') not in (None, 'None')]


def _names(text):
    return [n.strip() for n in str(text or '').split(',') if n.strip()]


def _objective(node, npc_ids):
    out = {'id': node.get('Id', 0), 't': node['Type'], 'v': _values(node)}
    if node.get('Optional'):
        out['opt'] = True
    if node.get('Key'):
        out['key'] = node['Key']
    if out['t'] in ('KillNpc', 'AskNpc', 'CloseNpc', 'HpRemaining') and out['v']:
        ids = [npc_ids[n] for n in _names(out['v'][0]) if n in npc_ids]
        if ids:
            out['npcs'] = ids
    post = _directives(node.get('PostDirectives'))
    if post:
        out['post'] = post
    pre = _directives(node.get('PreDirectives'))
    if pre:
        out['pre'] = pre
    return out


def convert(event, npc_ids):
    """One decoded event file -> the compact server record."""
    out = {'map': event.get('MapID'), 'start': _conditions(event.get('StartConditions')), 'fail': _conditions(event.get('FailConditions'))}
    rules = []
    for rule in event.get('GlobalConditions') or []:
        if isinstance(rule, dict) and rule.get('Directives'):
            rules.append({'c': _conditions(rule.get('Conditions')), 'd': _directives(rule['Directives'])})
    if rules:
        out['global'] = rules
    for key, name in (('OnComplete', 'complete'), ('OnFailure', 'failure')):
        directives = _directives(event.get(key))
        if directives:
            out[name] = directives
    if event.get('ValueKeys'):
        out['values'] = list(event['ValueKeys'])
    stages = []
    for stage in event.get('Stages') or []:
        record = {'id': stage.get('Id', 0), 'key': stage.get('Key') or '', 'steps': []}
        fail = _conditions(stage.get('FailConditions'))
        if fail:
            record['fail'] = fail
            record['goto'] = stage.get('FailGoToStage') or ''
            record['faildir'] = _directives(stage.get('FailDirectives'))
        if stage.get('RevivalPoint'):
            record['revive'] = stage['RevivalPoint']
        for step in stage.get('Steps') or []:
            entry = {'id': step.get('Id', 0), 'pre': _directives(step.get('PreDirectives')),
                     'obj': [_objective(o, npc_ids) for o in step.get('Objectives') or []],
                     'post': _directives(step.get('PostDirectives'))}
            fail = _conditions(step.get('FailConditions'))
            if fail:
                entry['fail'] = fail
                entry['faildir'] = _directives(step.get('FailDirectives'))
            record['steps'].append(entry)
        stages.append(record)
    out['stages'] = stages
    return out


def _walk(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from _walk(value)
    elif isinstance(node, list):
        for value in node:
            yield from _walk(value)


def npc_names(table=NPCDATA):
    """NpcData Name -> ID, and the NPCs that open an instance layer: ID -> (dungeon id, talk)."""
    names, entries = {}, {}
    for row in _walk(json.loads(Path(table).read_text(encoding='utf-8'))):
        if not (isinstance(row.get('ID'), dict) and isinstance(row.get('Name'), str) and 'FunctionType' in row):
            continue
        names[row['Name']] = row['ID']['Value']
        values = row.get('FuncValues') or []
        if _enum(row['FunctionType']) == 'EnterInstanceLayer' and values and str(values[0]).isdigit():
            entries[row['ID']['Value']] = {'dungeon': int(values[0]), 'talk': values[3] if len(values) > 3 else ''}
        elif _enum(row['FunctionType']) == 'EnterFieldEvent' and values:
            entries[row['ID']['Value']] = {'event': str(values[0]), 'talk': ''}   # the dungeon follows from FieldEvent.dat in build()
        elif _enum(row['FunctionType']) == 'NpcTeleport' and values and str(values[0]).isdigit():
            entries[row['ID']['Value']] = {'teleport': int(values[0]), 'talk': ''}   # the Abyss portals and the teleport managers
    return names, entries


ENTRANCES = ROOT / 'research/local/dungeon-entry-tables-20261018/table-001.json'   # MapEntrance.dat (Name = Enter_<dungeon name>)
REWARD_ITEMS = ROOT / 'research/local/dungeon-entry-tables-20261018/table-005.json'   # RewardItem.dat
CUBES = ROOT / 'research/local/dungeon-reward-tables-20261018/table-000.json'   # RewardOdEnergyCube.dat
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
REWARD_DUNGEON = ROOT / 'research/local/dungeon-tables/table-005.json'   # RewardDungeon.dat (RewardLight/RewardDark names)
QUEST_STEPS = ROOT / 'research/local/quest-tables/table-002.json'   # QuestStep.dat (MapEntrance QuestStepIdList ids)
FIELD_EVENTS = ROOT / 'research/local/dungeon-tables/table-004.json'   # FieldEvent.dat (ready/progress seconds by map)


def _optional_rows(path):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']
    except (OSError, ValueError, KeyError):
        return []


def _item_ids(catalog=CATALOG):
    ids = {}
    try:
        with open(catalog, encoding='utf-8') as source:
            for line in source:
                entry = json.loads(line)
                if entry.get('kind') == 'items':
                    ids[entry['internal_name']] = entry['id']
    except OSError:
        pass
    return ids


def cube_rewards(names, cubes=CUBES, reward_items=REWARD_ITEMS, catalog=CATALOG):
    """RewardOdEnergyCube name -> {'boss': final boss NpcData id, 'rewards': {npc id: [[item, prob of 1e8, min, max], ...]}}
    (RewardCommon for the final boss, RewardMiddleBossList for the mid bosses; RewardItem rows resolved to Item ids)."""
    ids = _item_ids(catalog)
    tables = {}
    for row in _optional_rows(reward_items):
        items = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1), int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                 for i in row.get('Items') or [] if i.get('ItemName') in ids and int(i.get('ItemProb') or 0) > 0]
        if items:
            tables.setdefault(row['Name'], []).extend(items)
    out = {}
    for row in _optional_rows(cubes):
        rewards = {}
        boss = names.get(row.get('FinalBossName'))
        if boss and row.get('RewardCommon') in tables:
            rewards[str(boss)] = tables[row['RewardCommon']]
        for mid in row.get('RewardMiddleBossList') or []:
            npc = names.get(mid.get('BossName'))
            if npc and mid.get('RewardName') in tables:
                rewards[str(npc)] = tables[mid['RewardName']]
        if boss:
            out[row['Name']] = {'boss': boss, 'rewards': rewards}
    return out


def dungeons(table=DUNGEONS, names=None, entrances=ENTRANCES, reward_dungeon=REWARD_DUNGEON, quest_steps=QUEST_STEPS):
    """Dungeon.dat rows the server uses, with the entry rules of MapEntrance.dat (Enter_<name>), the clear rewards of
    RewardDungeon.dat (RewardLight/RewardDark: [[item, count], ...]) and the reward cube (RewardOdEnergyCube.dat) of the
    expeditions; the optional tables are absent on a partial research transfer."""
    # MapEntrance.dat rows are their own ids (row 96 is a CommandPost layer, not dungeon 96): the link is the name,
    # Enter_<Dungeon.Name> (703 of 797 rows; the rest are test/design maps and layers without a Dungeon row).
    gates = {row['Name'][len('Enter_'):]: row for row in _optional_rows(entrances) if str(row.get('Name', '')).startswith('Enter_')}
    cubes = cube_rewards(names or {}) if names else {}
    ids = _item_ids()
    clear_rewards = {row['Name']: [[ids[i['Item']], int(i.get('Count') or 1)] for i in row.get('Items') or [] if i.get('Item') in ids]
                     for row in _optional_rows(reward_dungeon)}
    steps = {row['ID']['Value']: [row['QuestName'], int(row.get('Order') or 1)] for row in _optional_rows(quest_steps) if isinstance(row.get('ID'), dict)}
    field_events = {row['MapId']['Value']: row for row in _optional_rows(FIELD_EVENTS) if isinstance(row.get('MapId'), dict)}
    out = {}
    for row in json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']:
        dungeon_id = row['ID']['Value']
        entry = {'name': row['Name'], 'map': row['MapId']['Value'], 'type': _enum(row['DungeonType']),
                 'exit': _enum(row['ExitType']), 'start_light': row.get('LightStartPointName') or 'None',
                 'start_dark': row.get('DarkStartPointName') or 'None',
                 'hold': int(row.get('CompleteStateHoldingTime') or 0),
                 'limit': int(row.get('ProcessStateHoldingTime') or 0),
                 'group': row.get('DungeonGroupName') or 'None'}
        gate = gates.get(row['Name'])
        if gate:
            entry.update({'min_level': int(gate.get('MinLevel') or 0), 'max_level': int(gate.get('MaxLevel') or 0),
                          'item_level': int(gate.get('EquipItemLevel') or 0), 'max_member': int(gate.get('MaxMember') or 0)})
            quest_gate = [steps[s['Value']] for s in gate.get('QuestStepIdList') or [] if isinstance(s, dict) and s.get('Value') in steps]
            if quest_gate:
                entry['quest_gate'] = quest_gate
        for side, key in (('RewardLight', 'reward_light'), ('RewardDark', 'reward_dark')):
            if clear_rewards.get(row.get(side)):
                entry[key] = clear_rewards[row[side]]
        field_event = field_events.get(entry['map'])
        if field_event:
            entry.update({'event': field_event['EventName'], 'ready': int(field_event.get('ReadyDuration') or 0), 'progress': int(field_event.get('ProgressTime') or 0)})
        cube = cubes.get(row.get('RewardOdEnergyCube'))
        if cube:
            entry['cube_boss'] = cube['boss']
            entry['cube'] = cube['rewards']
        if row['Name'] in ARRIVAL_CUTSCENES:
            entry['arrival_cutscene'] = ARRIVAL_CUTSCENES[row['Name']]
        out[dungeon_id] = entry
    return out


def mapdata_paths(index=MAPDATA_INDEX, root=MAPDATA):
    files = json.loads(Path(index).read_text(encoding='utf-8'))
    return {int(k): Path(root) / v[0].replace('\\', '/') for k, v in files.items() if v}


def directive_spawners(event):
    """Spawn entry names a StartSpawner directive of the event names (deferred spawns)."""
    names = set()
    for node in _walk(event):
        if isinstance(node, dict) and node.get('Type') in SPAWNER_DIRECTIVES and node.get('value_list'):
            names.update(_names(_values(node)[0]))
    return names


def map_facts(data):
    """Teleport points, the instance-layer volume and the spawn entry names of a decoded MapData."""
    points = {}
    for entry in data.get('TeleportDataMap') or []:
        loc = entry['Value']['Location']
        points[entry['Key']] = [round(loc['X'], 1), round(loc['Y'], 1), round(loc['Z'], 1)]
    volume = None
    for entry in data.get('InstanceLayerVolumeInfoMap') or []:
        value = entry['Value']
        volume = {'base': value['BaseMapId'], 'points': [[round(p['X'], 1), round(p['Y'], 1)] for p in value.get('Points') or []],
                  'zmin': value.get('HeightMin', 0), 'zmax': value.get('HeightMax', 0)}
        break
    spawns = {record['Name'] for record in data.get('SpawnInfoList') or [] if record.get('Name')}
    return points, volume, spawns


def entry_triggers(data, map_id):
    """EnterInstanceLayer trigger actors of a (base) map: [{dungeon, trigger, center, shape, box, radius, height}]."""
    out = []
    for entry in data.get('TriggerActorDataMap') or []:
        value = entry['Value']
        if _enum(value.get('FuncType')) != 'EnterInstanceLayer' or not value.get('DungeonId', {}).get('Value'):
            continue
        loc, box = value['Location'], value.get('BoxSize') or {}
        out.append({'dungeon': value['DungeonId']['Value'], 'trigger': value.get('Name', ''),
                    'center': [round(loc['X'], 1), round(loc['Y'], 1), round(loc['Z'], 1)], 'direction': round(value.get('Direction', 0.0), 3),
                    'shape': _enum(value.get('CollisionType')), 'box': [round(box.get('X', 0.0), 1), round(box.get('Y', 0.0), 1)],
                    'radius': round(value.get('Radius', 0.0), 1), 'height': round(value.get('Height', 0.0), 1)})
    return out


def build(raw=RAW, dungeon_table=DUNGEONS, npc_table=NPCDATA, served_types=SERVED_TYPES):
    names, npc_entries = npc_names(npc_table)
    rooms = dungeons(dungeon_table, names)
    # EnterFieldEvent NPCs name their event; the event's map is a FieldEvent dungeon row.
    room_by_event = {room['event']: dungeon_id for dungeon_id, room in rooms.items() if room.get('event')}
    for npc_id, entry in list(npc_entries.items()):
        if 'event' in entry:
            if entry['event'] in room_by_event:
                entry['dungeon'] = room_by_event[entry['event']]
            else:
                del npc_entries[npc_id]
    paths = mapdata_paths()
    events, by_map, deferred = {}, {}, {}
    for path in sorted(Path(raw).glob('*.dat')):
        event = decode(path.read_bytes())
        name = event.get('Name') or path.stem
        events[name] = convert(event, names)
        by_map.setdefault(event.get('MapID'), []).append(name)
        deferred.setdefault(event.get('MapID'), set()).update(directive_spawners(event))
    room_by_map = {}
    for dungeon_id, room in rooms.items():
        room_by_map.setdefault(room['map'], dungeon_id)
    # Served: every dungeon map of a served type that has MapData (with or without scripts), except the shared Abyss zones.
    served = sorted({m for m in by_map if m in room_by_map and rooms[room_by_map[m]]['type'] in served_types}
                    | {room['map'] for room in rooms.values() if room['type'] in served_types and room['map'] in paths})
    points, volumes, spawners, entries = {}, {}, {}, {}
    for map_id in served:
        if map_id not in paths:
            continue
        data = json.loads(paths[map_id].read_text(encoding='utf-8'))['Properties']['Data']
        map_points, volume, spawns = map_facts(data)
        if map_points:
            points[str(map_id)] = map_points
        if volume:
            volumes[str(map_id)] = volume
        waiting = sorted(deferred.get(map_id, set()) & spawns)
        if waiting:
            spawners[str(map_id)] = waiting
    teleports = teleport_targets(events, points, paths)
    effects = teleport_effects(events)
    base_maps = {volume['base'] for volume in volumes.values()} | {1000, 1010, 1011, 1100, 1110, 1111}
    for map_id in sorted(base_maps):
        if map_id in paths:
            data = json.loads(paths[map_id].read_text(encoding='utf-8'))['Properties']['Data']
            triggers = entry_triggers(data, map_id)
            if triggers:
                entries[str(map_id)] = triggers
    return {
        'source': 'Data/MapEvent/*.dat (XOR-masked JSON), Dungeon.dat, NpcData.dat FunctionType EnterInstanceLayer, MapData.dat '
                  'TeleportDataMap/InstanceLayerVolumeInfoMap/TriggerActorDataMap(EnterInstanceLayer)',
        'served_types': list(served_types),
        'served_maps': served,
        'events': events,
        'maps': {str(k): v for k, v in sorted(by_map.items(), key=lambda kv: (kv[0] is None, kv[0]))},
        'dungeons': {str(k): v for k, v in rooms.items()},
        'npc_entries': {str(k): v for k, v in npc_entries.items()},
        'entries': entries,
        'points': points,
        'volumes': volumes,
        'deferred': spawners,
        'teleports': teleports,
        'teleport_effects': effects,
    }


def teleport_effects(events, table=TELEPORT_EFFECTS):
    """The TeleportEffect.dat ids of the effect names PCTeleport directives use: name -> id."""
    if not Path(table).exists():
        return {}
    ids = {row['Name']: row['ID']['Value'] for row in json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']}
    used = set()
    for event in events.values():
        for name in re.findall(r'"t": "PCTeleport", "v": \["[^"]*", "([^"]*)"', json.dumps(event)):
            if name in ids:
                used.add(name)
    return {name: ids[name] for name in sorted(used)}


def teleport_targets(events, points, paths, table=TELEPORTS):
    """PCTeleport names the event's own map has no point for, resolved through Teleport.dat: name -> {map, at}."""
    if not Path(table).exists():
        return {}
    rows = {row['Name']: (row['MapId']['Value'], row['ActorName']) for row in json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']}
    wanted = set()
    for event in events.values():
        own = points.get(str(event.get('map')), {})
        for name in re.findall(r'"t": "PCTeleport", "v": \["([^"]*)"', json.dumps(event)):
            if name and name not in own and name in rows:
                wanted.add(name)
    actors = {}
    out = {}
    for name in sorted(wanted):
        map_id, actor = rows[name]
        if map_id not in actors:
            actors[map_id] = map_facts(json.loads(paths[map_id].read_text(encoding='utf-8'))['Properties']['Data'])[0] if map_id in paths else {}
        at = actors[map_id].get(actor)
        if at is None and actor != 'PlayerStart':
            continue
        out[name] = {'map': map_id, 'at': at}
    return out


def served_maps(path=DATA):
    """The instance maps the server runs map events on (for the spawn and object extractors)."""
    try:
        return tuple(json.loads(Path(path).read_text(encoding='utf-8')).get('served_maps') or ())
    except (OSError, ValueError):
        return ()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    out = build()
    DATA.write_text(json.dumps(out, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    print(len(out['events']), 'events on', len(out['maps']), 'maps;', len(out['served_maps']), 'served maps,',
          sum(len(v) for v in out['entries'].values()), 'entry triggers,', len(out['npc_entries']), 'entry NPCs ->', DATA.relative_to(ROOT),
          DATA.stat().st_size // 1024, 'KB')


if __name__ == '__main__':
    main()
