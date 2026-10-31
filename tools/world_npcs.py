"""NPC spawns for a map from the client's own data, for the development server.

Derived from offline exports (see research/world-recovery.md):
- MapData.dat SpawnInfoList (research/local/reuse-mapdata-probe for Poeta, and
  research/local/all-mapdata-full for the other field maps: FIELD_MAPS): spawn name, NpcData ID,
  position and yaw. Poeta (map 1000) has 274 NPC records with one position each; the levelling maps
  have thousands (the server streams them by distance).
- NpcData.dat (research/local/npc-table): level, NpcType, RelationshipEntity, AggroType,
  AggroDetectRange, ActiveSkillList, speeds, collision, rotation limits.
- NpcAggroData.dat (reuse-table-probe table-012): the aggro shape each AggroDetectRange names.
- Skill.dat / SkillEffect.dat (table-007 / table-008): range, follow range, cooldown, cast time,
  damage effect ID and value range of every NPC skill the spawns use.

Not in the client data, so ours: HP and NPC damage (combat calibration). HP_BY_LEVEL is OBSERVED in the official
recording (research/official-combat-capture.md, 0x3641 HP of ordinary NPCs, all
multiples of 133); other levels are interpolated. Spawn conditions (initial spawn,
quest phases), respawn timers and AI live elsewhere (map cell AionSpawnUnit actors,
server rules) and are not applied: every record spawns at world entry.

    python tools/world_npcs.py build     # writes tools/data/world-npcs.json

File format (compact): templates {npc_id: the NpcData fields}, maps {map: [[name, npc_id, x, y, z, yaw, unique_key,
social_action, route], ...]}, routes {map: [{name, points [[x, y, z]], traverse, run}]}, npc_skills. data() expands it to one
dict per spawn as before.

Official NPC behaviour (research/local/npc-behaviour-20261017, capture 20261013-144836):
- unique_key: MapData SpawnInfoList UniqueKey, sent in NpcAppear_NT as spawn_unique_key (low 32 bits). OBSERVED.
- social_action: the looping idle animation set once in the appear (mask 0x0400), from tools/data/npc-social-actions.json
  (the ids seen per spawn; no client table assigns them). 0 = none.
- route: index into routes of the map, or -1. A spawn whose name contains OnWay / Roam walks the MapData WaypointsDataMap
  set (WaypointType NPC) whose first point is nearest, within ROUTE_LINK_RANGE (CANDIDATE rule: no data field links them;
  it reproduces the official patrol N_L0_R3_Legion_M_02_OnWay on L0_R4_Legion_Waypoints_1).
"""
import argparse
import json
import sys
from functools import lru_cache
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/world-npcs.json'
MAPDATA = {1000: ROOT / 'research/local/reuse-mapdata-probe/table-001.json'}
ALL_MAPDATA = ROOT / 'research/local/all-mapdata-full'
# The open world: Light starter and the two Light field maps, Dark starter and the two Dark field maps.
FIELD_MAPS = (1010, 1011, 1100, 1110, 1111, 20, 22, 23)   # the Reshanta Abyss maps are shared field maps (Dungeon.dat type Abyss)
# Instance layers the server runs (tools/data/instances.json): one copy per player.
MAP_EVENTS = ROOT / 'tools/data/map-events.json'


def _served_instance_maps(path=MAP_EVENTS):
    """The instance layers and dungeons the server runs map events on (tools/map_events.py); the first layer without it."""
    try:
        return tuple(json.loads(Path(path).read_text(encoding='utf-8')).get('served_maps') or ()) or (100002,)
    except (OSError, ValueError):
        return (100002,)


INSTANCE_MAPS = _served_instance_maps()
SPAWN_FIELDS = ('name', 'npc_id', 'x', 'y', 'z', 'yaw', 'unique_key', 'social_action', 'route')
SOCIAL_ACTIONS = ROOT / 'tools/data/npc-social-actions.json'
ROUTE_LINK_RANGE = 300.0
ROUTE_SPAWN_WORDS = ('onway', 'roam')


def extra_template_ids():
    """NpcData ids with no placement that the server spawns (tools/bosschallenge_data.py's bosses)."""
    try:
        from bosschallenge_data import boss_npc_ids
        return boss_npc_ids()
    except Exception:   # a partial research transfer without the tables
        return []


def field_mapdata(maps=FIELD_MAPS + INSTANCE_MAPS, source=ALL_MAPDATA):
    """{map id: decoded MapData table} for the field maps (and served instance layers) in the full MapData export."""
    found = {}
    for summary in sorted(Path(source).glob('*/summary.json')):
        for package in json.loads(summary.read_text(encoding='utf-8'))['packages']:
            # Field maps live under World/..., the expeditions under Dungeon/...: any MapData.dat of a wanted map counts.
            if not package['path'].endswith('MapData.dat'):
                continue
            path = summary.parent / package['file']
            try:
                map_id = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data'].get('MapId')
            except (OSError, ValueError, KeyError):
                continue
            if map_id in maps:
                found[map_id] = path
    return found
NPCDATA = ROOT / 'research/local/npc-table/table-000.json'
TABLES = ROOT / 'research/local/reuse-table-probe'
AGGRODATA = TABLES / 'table-012.json'
SKILLDATA = TABLES / 'table-007.json'
EFFECTDATA = TABLES / 'table-008.json'
FIRST_KEY = 2000   # entity keys: 1 is the player, 1000 the training dummy

# ERelationshipEntity ordinals (usmap); 6/3/11/13 also observed on the wire.
# NpcData RelationshipEntity to the wire byte (the full ERelationshipEntity list is in server/Aion.World/MapEvents.cs).
# Names missing here appear as 6 Monster; PC_FriendlyToAll added 2026-10-24 (official 23 for the Ascension Subspace's flight
# checkpoints, which appeared as hostile monsters). The player-faction and role names stay 6: our hostility ignores factions.
RELATIONSHIP = {'NoRelation': 0, 'NPC_Light': 3, 'NPC_Dark': 4, 'NPC_Neutral': 5, 'Monster': 6,
                'Monster_AntiCompanion': 11, 'Monster_PCFriendly': 12, 'NPC_Companion': 13,
                'NPC_NoInteraction': 14, 'Monster_TeamA': 16, 'Monster_TeamB': 17,
                'Monster_TeamC': 18, 'Monster_TeamD': 19, 'Monster_TeamE': 20, 'PC_FriendlyToAll': 23,
                'NPC_Light_Guard': 33, 'NPC_Dark_Guard': 34}
# Story companions (NpcData NPC_Companion that can be attacked) appear with 999999 HP and are healed back to it after
# each hit (OBSERVED: 18 of 18 in the 2026-10-19 recording, Nania and Salros taking hits in HQ002; the NPC_Companion that
# cannot be attacked, and every other citizen, have level HP).
COMPANION_HP = 999999


def companion_hp(row):
    return row['RelationshipEntity'].endswith('::NPC_Companion') and bool(row.get('bCanBeAttacked'))


# Official 0x3641 HP by NpcData level for Grade 3 NormalMonsters, in units of 133 (266 = 133 x 2, 399 = 133 x 3, ...).
# OBSERVED 2026-10-13 and in the 2026-10-19 recording (levels 1, 6, 8 added; 45 from a Grade 1 monster's 26600 / 50 x 133).
HP_BY_LEVEL = {1: 133, 3: 199, 4: 266, 5: 399, 6: 532, 7: 665, 8: 798, 9: 931, 10: 1064, 15: 2660, 16: 3059, 30: 9975, 45: 70756}
# HP per level unit by (NpcSubType, Grade), OBSERVED in the 2026-10-19 recording: Grade 1 NormalMonsters 50 per unit
# (level 7: 250, level 8: 300, level 1: 50), Grade 2 75 (150, 225, 375, 450), Grade 3 133 (the curve); the Grade 1
# EliteMonster 2290016 (level 6) 3200 = 800 x 4; the Grade 5 HeroMonster 2701034 (the lake cave boss, level 5) 3000 = 1000 x 3.
# Other combinations fall back to the sub type's observed unit (Legend: OURS).
HP_UNIT = {('NormalMonster', 1): 50, ('NormalMonster', 2): 75, ('NormalMonster', 3): 133, ('EliteMonster', 1): 800, ('HeroMonster', 5): 1000}
HP_UNIT_BY_SUB_TYPE = {'NormalMonster': 133, 'EliteMonster': 800, 'HeroMonster': 1000, 'LegendMonster': 2000}
HP_BASE_UNIT = 133


def hp_unit(sub_type, grade):
    """HP per level unit of an NPC rank (HP_UNIT, else the sub type's unit)."""
    return HP_UNIT.get((sub_type, grade), HP_UNIT_BY_SUB_TYPE.get(sub_type, HP_BASE_UNIT))


def hp_for(level, sub_type='NormalMonster', grade=3):
    """An NPC's max HP: the level's Grade 3 curve scaled by its rank's unit."""
    return max(1, round(hp_for_level(level) * hp_unit(sub_type, grade) / HP_BASE_UNIT))


def hp_for_level(level):
    """Observed HP, linearly interpolated (and extrapolated) between observed levels."""
    if level in HP_BY_LEVEL:
        return HP_BY_LEVEL[level]
    known = sorted(HP_BY_LEVEL)
    low = max([k for k in known if k < level], default=known[0])
    high = min([k for k in known if k > level], default=known[-1])
    if low == high:
        return max(1, round(HP_BY_LEVEL[low] * level / low))
    t = (level - low) / (high - low)
    return round(HP_BY_LEVEL[low] + t * (HP_BY_LEVEL[high] - HP_BY_LEVEL[low]))


def _find(node, key):
    if isinstance(node, dict):
        for name, value in node.items():
            if name == key:
                return value
            found = _find(value, key)
            if found is not None:
                return found
    return None


def _rows(path):
    return _find(json.loads(path.read_text(encoding='utf-8')), 'Data')


def _enum(value):
    return value.split('::')[-1]


def aggro_shape(row):
    """NpcAggroData row -> shape the server tests: arc (degrees, centred on the facing) or circle."""
    info = row['AggroRangeInfo']
    return {'shape': _enum(row['AggroRangeType']), 'radius': info['Radius'], 'angle': info['Angle'],
            'inner': info['InnnerRadius'], 'height': info['Height'], 'z': _enum(row['AggroRangeZAxis'])}


def npc_skills(skill_ids, skill_table=SKILLDATA, effect_table=EFFECTDATA):
    """Range/cooldown/cast and the damage effect of each NPC skill (first damage effect group)."""
    wanted = set(skill_ids)
    skills = {row['ID']['Value']: row for row in _rows(skill_table) if row['ID']['Value'] in wanted}
    groups = {g['SkillEffectGroupId']['Value'] for row in skills.values() for g in row.get('SkillEffectTimeDataList', [])}
    damage = {}
    for effect in _rows(effect_table):
        group = effect['SkillEffectGroupId']['Value']
        if group in groups and _enum(effect['EffectType']) == 'Damage' and group not in damage:
            values = effect.get('EffectValueList', [])
            damage[group] = (effect['ID']['Value'], int(values[2]) if len(values) > 3 else 10000,
                             int(values[3]) if len(values) > 3 else 10000)
    out = {}
    for skill_id, row in sorted(skills.items()):
        group = next((g['SkillEffectGroupId']['Value'] for g in row.get('SkillEffectTimeDataList', [])
                      if g['SkillEffectGroupId']['Value'] in damage), None)
        effect, low, high = damage.get(group, (0, 0, 0))
        out[str(skill_id)] = {'range': row.get('NeedSkillUseRange', 0.0), 'follow': row.get('NeedSkillFollowRange', 0.0),
                              'cooldown': int(row.get('NeedCoolTime', 0)), 'cast': int(row.get('CastingTime', 0)),
                              'effect': effect, 'damage_min': low, 'damage_max': high}
    return out


def routes(mapdata):
    """NPC waypoint sets of a decoded MapData file: [{name, points, traverse, run}]."""
    out = []
    for entry in _find(mapdata, 'WaypointsDataMap') or []:
        value = entry.get('Value', {}) if isinstance(entry, dict) else {}
        if not str(value.get('WaypointType', '')).endswith('NPC') or not value.get('Points'):
            continue
        out.append({'name': entry.get('Key') or value.get('LabelName', ''),
                    'points': [[round(pt['X'], 1), round(pt['Y'], 1), round(pt['Z'], 1)] for pt in value['Points']],
                    'traverse': str(value.get('TraverseType', 'EWaypointTraverseType::OneWay')).split('::')[-1],
                    # MoveType 2 ran (the HQ001 quest routes), 1 walked (the patrol). OBSERVED.
                    'run': value.get('MoveType') == 2})
    return out


def route_for(name, position, map_routes):
    """The route a roaming spawn walks: the set whose first point is nearest, within ROUTE_LINK_RANGE; else -1."""
    if not any(word in name.lower() for word in ROUTE_SPAWN_WORDS) or not map_routes:
        return -1
    def distance(route):
        x, y, _ = route['points'][0]
        return ((x - position[0]) ** 2 + (y - position[1]) ** 2) ** 0.5
    best = min(range(len(map_routes)), key=lambda i: distance(map_routes[i]))
    return best if distance(map_routes[best]) <= ROUTE_LINK_RANGE else -1


def build(maps=MAPDATA, npc_table=NPCDATA, aggro_table=AGGRODATA, skill_table=SKILLDATA, effect_table=EFFECTDATA, social=SOCIAL_ACTIONS):
    npcs = {row['ID']['Value']: row for row in _rows(npc_table)}
    shapes = {row['AggroDetectType']: aggro_shape(row) for row in _rows(aggro_table)}
    used_skills = set()
    out = {'source': {'mapdata': 'MapData.dat SpawnInfoList (FAion2MapDataFile)',
                      'npcdata': 'NpcData.dat level/type/relationship/aggro',
                      'hp': 'official 0x3641 HP by level, interpolated',
                      'aggro': 'NpcAggroData.dat by NpcData.AggroDetectRange',
                      'npc_skills': 'Skill.dat + SkillEffect.dat for NpcData.ActiveSkillList'},
           'maps': {}, 'routes': {}}
    social_actions = json.loads(Path(social).read_text(encoding='utf-8'))['maps'] if Path(social).exists() else {}
    templates = {}
    for map_id, path in maps.items():
        spawns = []
        mapdata = json.loads(path.read_text(encoding='utf-8'))
        map_routes = routes(mapdata)
        actions = social_actions.get(str(map_id), {})
        for record in _find(mapdata, 'SpawnInfoList'):
            # A record with several NpcData ids spawns one NPC per position, not every id at every position
            # (OBSERVED: the HQ001 layer's Cherubim_02 group, 2 ids x 4 positions, appeared as 4 NPCs). Which id
            # each position gets is CANDIDATE (the official pick looked random); the ids are cycled here.
            ids = [entry['Value'] for entry in record['NpcIdList']]
            if not ids:
                continue
            for index, position in enumerate(record['Positions']):
                npc_id = ids[index % len(ids)]
                entry = {'Value': npc_id}
                row = npcs[npc_id]
                relationship = row['RelationshipEntity'].split('::')[1]
                skills = [s['Value'] for s in row.get('ActiveSkillList', []) if s['Value']]
                templates.setdefault(str(entry['Value']), {
                                   'level': row['Level'],
                                   'type': row['NpcType'].split('::')[1],
                                   'relationship': RELATIONSHIP.get(relationship, 6),
                                   'aggressive': row['AggroType'].endswith(('Aggro_A', 'Aggro_A_SearchOnlyPC')),
                                   # Units/s; the official 0x371b-c walk/run moves match these.
                                   'walk_speed': row.get('BaseWalkSpeed', 0.0),
                                   'run_speed': row.get('BaseMoveSpeed', 0.0),
                                   'radius': row.get('CollisionCylinderRadius', 0),
                                   'function': row.get('FunctionType', 'ENpcFuncType::None').split('::')[-1],
                                   'aggro_type': _enum(row.get('AggroType', 'EAggroType::None')),
                                   'aggro': shapes.get(row.get('AggroDetectRange')),
                                   'skills': skills,
                                   'height': row.get('CollisionCylinderHeight', 0),
                                   'no_rotation': bool(row.get('bNoRotation', False)),
                                   'rotate_max': row.get('RotateAngleMax', 0),
                                   'sub_type': _enum(row.get('NpcSubType', 'ENpcSubType::NormalMonster')),
                                   'grade': int(row.get('Grade') or 3),
                                   **({'air': True} if row.get('ObjectTerrainType', '').endswith('::Air') else {}),
                                   **({'hp_max': COMPANION_HP} if companion_hp(row) else {})})
                used_skills.update(skills)
                unique_key = int(record.get('UniqueKey', 0)) & 0xffffffff
                location = position['Location']
                at = (location['X'], location['Y'])
                spawns.append([record['Name'], npc_id, round(location['X'], 3), round(location['Y'], 3),
                               round(location['Z'], 3), round(position['Rotation']['Yaw'] % 360, 3), unique_key,
                               actions.get(str(unique_key), 0), route_for(record['Name'], at, map_routes)])
        # Only the routes a spawn walks (quest-scene routes and unlinked sets stay out), renumbered. Instance maps keep
        # every named set: map event directives (NpcMoveToWayPoint) walk them by name.
        used = sorted({spawn[8] for spawn in spawns if spawn[8] >= 0} | (set(range(len(map_routes))) if map_id in INSTANCE_MAPS else set()))
        renumber = {old: new for new, old in enumerate(used)}
        for spawn in spawns:
            spawn[8] = renumber.get(spawn[8], -1)
        out['maps'][str(map_id)] = spawns
        out['routes'][str(map_id)] = [map_routes[i] for i in used]
    # NPCs the server spawns itself (the boss challenge bosses are placed nowhere): their templates too.
    for npc_id in extra_template_ids():
        row = npcs.get(npc_id)
        if row is None or str(npc_id) in templates:
            continue
        relationship = row['RelationshipEntity'].split('::')[1]
        skills = [s['Value'] for s in row.get('ActiveSkillList', []) if s['Value']]
        templates[str(npc_id)] = {'level': row['Level'], 'type': row['NpcType'].split('::')[1], 'relationship': RELATIONSHIP.get(relationship, 6),
                                  'aggressive': row['AggroType'].endswith(('Aggro_A', 'Aggro_A_SearchOnlyPC')), 'walk_speed': row.get('BaseWalkSpeed', 0.0),
                                  'run_speed': row.get('BaseMoveSpeed', 0.0), 'radius': row.get('CollisionCylinderRadius', 0),
                                  'function': row.get('FunctionType', 'ENpcFuncType::None').split('::')[-1], 'aggro_type': _enum(row.get('AggroType', 'EAggroType::None')),
                                  'aggro': shapes.get(row.get('AggroDetectRange')), 'skills': skills, 'height': row.get('CollisionCylinderHeight', 0),
                                  'no_rotation': bool(row.get('bNoRotation', False)), 'rotate_max': row.get('RotateAngleMax', 0),
                                  'sub_type': _enum(row.get('NpcSubType', 'ENpcSubType::NormalMonster')), 'grade': int(row.get('Grade') or 3),
                                  **({'air': True} if row.get('ObjectTerrainType', '').endswith('::Air') else {}),
                                   **({'hp_max': COMPANION_HP} if companion_hp(row) else {})}
        used_skills.update(skills)
    out['templates'] = templates
    out['npc_skills'] = npc_skills(used_skills, skill_table, effect_table)
    return out


def expand(raw):
    """The compact file as one dict per spawn (template fields + name, npc_id, position, yaw)."""
    templates = raw.get('templates', {})
    maps = {}
    for map_id, spawns in raw['maps'].items():
        # Entries already in the expanded form (older files, test fixtures) pass through.
        maps[map_id] = [s if isinstance(s, dict) else dict(templates[str(s[1])], name=s[0], npc_id=s[1], position=[s[2], s[3], s[4]], yaw=s[5],
                                                           unique_key=s[6] if len(s) > 6 else 0, social_action=s[7] if len(s) > 7 else 0,
                                                           route=s[8] if len(s) > 8 else -1)
                        for s in spawns]
    return dict(raw, maps=maps)


@lru_cache(maxsize=1)
def data():
    return expand(json.loads(DATA.read_text(encoding='utf-8')))


def map_targets(map_id, target_class):
    """Fresh `target_class` (combat.Target) objects for every NPC spawn on a map."""
    targets = {}
    for index, spawn in enumerate(data()['maps'].get(str(map_id), [])):
        template = data()['templates'].get(str(spawn['npc_id']), {})
        hp = hp_for(spawn['level'], spawn.get('sub_type') or template.get('sub_type', 'NormalMonster'), template.get('grade', 3))
        key = FIRST_KEY + index
        targets[key] = target_class(key=key, name=spawn['name'], hp=hp, hp_max=hp,
                                    position=tuple(spawn['position']), npc_id=spawn['npc_id'],
                                    level=spawn['level'], direction=spawn['yaw'],
                                    relationship=spawn['relationship'], aggressive=spawn['aggressive'],
                                    walk_speed=spawn.get('walk_speed', 0.0),
                                    run_speed=spawn.get('run_speed', 0.0),
                                    radius=spawn.get('radius', 0),
                                    function=spawn.get('function', 'None'))
    return targets


def infer_skill_hits(effect, skill_id=0):
    """When Skill.dat is unavailable: multi-hit rows share effect ids +10 per swing (123170011, 021, 031)."""
    if effect <= 0:
        return []
    hits = [{'effect': effect, 'projectile_id': 0, 'groggy': 0}]
    n = effect
    for _ in range(2):
        n += 10
        if skill_id and n // 100 != effect // 100:
            break
        if n % 10 == 0:
            break
        hits.append({'effect': n, 'projectile_id': 0, 'groggy': 0})
    return hits


def skill_hits_catalog(skill_ids, skill_table=SKILLDATA, effect_table=EFFECTDATA):
    """SkillEffectTimeDataList damage rows and ProjectileId per NPC skill (tools/world_npcs.py hits --catalog)."""
    if not Path(skill_table).exists() or not Path(effect_table).exists():
        out = {}
        for sid in skill_ids:
            key = str(sid)
            row = npc_skills([sid], skill_table, effect_table).get(key)
            if row and row.get('effect'):
                out[key] = infer_skill_hits(row['effect'], sid)
        return out
    skills = {row['ID']['Value']: row for row in _rows(skill_table) if row['ID']['Value'] in skill_ids}
    groups = {}
    for effect in _rows(effect_table):
        group = effect['SkillEffectGroupId']['Value']
        if _enum(effect['EffectType']) == 'Damage':
            groups.setdefault(group, []).append(effect)
        elif _enum(effect['EffectType']) == 'GroggyGuardDecreaseEnergy':
            val = effect.get('EffectValueList') or []
            groups.setdefault(group, []).append({'groggy': int(val[0]) if val else 0})
    out = {}
    for skill_id, row in sorted(skills.items()):
        hits = []
        for timed in row.get('SkillEffectTimeDataList', []):
            group = timed['SkillEffectGroupId']['Value']
            dmg = next((e for e in groups.get(group, []) if isinstance(e, dict) and 'ID' in e), None)
            if dmg is None:
                continue
            values = dmg.get('EffectValueList', [])
            groggy_row = next((e for e in groups.get(group, []) if isinstance(e, dict) and 'groggy' in e), None)
            projectile = int(_value(timed.get('ProjectileId', 0)) or 0)
            hits.append({'effect': dmg['ID']['Value'],
                         'projectile_id': projectile,
                         'groggy': groggy_row['groggy'] if groggy_row else 0})
        if not hits and str(skill_id) in npc_skills([skill_id], skill_table, effect_table):
            entry = npc_skills([skill_id], skill_table, effect_table)[str(skill_id)]
            hits = infer_skill_hits(entry.get('effect', 0), skill_id)
        if hits:
            out[str(skill_id)] = hits
    return out


def hits_catalog(skill_table=SKILLDATA, effect_table=EFFECTDATA, data_path=DATA):
    raw = json.loads(Path(data_path).read_text(encoding='utf-8')) if Path(data_path).exists() else {'npc_skills': {}}
    skill_ids = {int(k) for k in raw.get('npc_skills', {})}
    return skill_hits_catalog(skill_ids, skill_table, effect_table)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command')
    sub.add_parser('build', help='write tools/data/world-npcs.json')
    hits = sub.add_parser('hits', help='skill hit catalog from Skill.dat')
    hits.add_argument('--catalog', action='store_true', help='print skill_hits json for npc_skills')
    args = parser.parse_args()
    if args.command == 'build':
        out = build({**MAPDATA, **field_mapdata()})
        used = {int(k) for k in out.get('npc_skills', {})}
        out['skill_hits'] = skill_hits_catalog(used, skill_table=SKILLDATA, effect_table=EFFECTDATA)
        write_data_json(DATA, out)
        print({map_id: len(spawns) for map_id, spawns in out['maps'].items()}, 'spawns ->', DATA.relative_to(ROOT))
    elif args.command == 'hits' and args.catalog:
        print(json.dumps(hits_catalog(), indent=2, sort_keys=True))
    else:
        parser.error('command required')


if __name__ == '__main__':
    main()
