"""Shugo festival mini-games (FieldEvent.dat kind ShugoFesta and the Direct-entrance Pang / Dilemma events) ->
tools/data/festival-games.json for the server (server/Aion.World/FestivalGames.cs, GameSession.FestivalGames.cs).

  python tools/festival_games.py [--tables research/local/tables-20261109-023531]

Everything here is CLIENT DATA (the flat table export: Table_*.json, Map_Dungeon_FieldEvent_*_MapData.json); what the client
data does not say is marked CANDIDATE (the official game messages inside an event map are NOT recorded: every official
recording ends with the move to the dungeon server, 20261106-105448 #80582).
Per event map:
- event: FieldEvent.dat id, contents type (EEventContentsType) and value, ProgressTime, ReadyDuration, the reward cube group.
- spawners: MapData SpawnInfoList (name -> NPC ids, EnvObjData ids, position count, TrapBoardId) - the checks count them.
- pickups: the map's Overlap objects (EnvObjData Usage Overlap) that are point items, with their points (CANDIDATE values:
  no points table exists in the client; the rule texts only say "earn points for every ...").
- kills: NpcData ids of the map's monsters with their points (CANDIDATE values, by the NPC's role/colour).
- phases: the EventFunc.dat groups of the game (StartSpawner / EndSpawner / PlayNpcTalk / AddAbnormal / RemoveAbnormal rows)
  in table order. What triggers them is server-only data (no client table names them): the server runs them spread evenly
  over the play time (CANDIDATE).
- trapboard: FieldEventTrapBoard.dat rounds of the contents value, TrapBoard.dat grid of each round's board and the map's board
  spawner (SpawnInfo TrapBoardId), the bonus spawners (BonusEnv<round>), the round abnormals (EventFunc FE_Trapboard_*).
- racing: FieldEventRacing.dat (laps, start / end counts) and the map's lap triggers (TriggerActorDataMap *_Lap<n>_Trigger_<i>).
- platformer: FieldEventPlatformer.dat (wait / countdowns, CheckPointList) with the checkpoints' trigger volumes.
- dilemma: FieldEventPlatformerDilemma.dat rounds, the answer boards (SpawnInfo *_board_<n>), the round bonus Od spawners.
- masquerade / shooting: FieldEventMasquerade.dat WaitTime, FieldEventShooting.dat countdowns.
- vessels: NPCs of ENpcSubType Vessel and the MapData Vessel waypoint set starting at their spawn (moving platforms).
- rewards: RewardDataMonsterCube.dat rows of the event's RewardMonsterCube group (level bands) -> RewardItem.dat RewardCommon /
  RewardKey items resolved to Item ids; Post.dat 10000000 + event id (UseContents MonsterCube_FieldEvent) is the reward mail.
"""
import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
OUT = ROOT / 'tools/data/festival-games.json'

# CANDIDATE point values (no client table): the Od bubble / balloon variant number x 10, the bonus orbs more.
PICKUP_POINTS = [
    (re.compile(r'TrapBoard_01_Bonus2$'), 100),
    (re.compile(r'TrapBoard_01_Bonus$'), 50),
    (re.compile(r'Dilemma_YellowOd'), 20),
    (re.compile(r'Dilemma_GreenOd'), 10),
    (re.compile(r'Masq_OdBubble_003'), 30),
    (re.compile(r'Masq_AllBubble'), 10),
    (re.compile(r'OdBubble_?0*(\d+)'), None),   # variant number x 10
]
# CANDIDATE kill points by NpcData name (Pang colours: 01 blue, 01_V01 purple, 02 red, 03 gold; the Rotan is dodged, 0).
KILL_POINTS = [
    (re.compile(r'Pang_Rotan'), 0),
    (re.compile(r'Pang_Pang_01_V01'), 20),
    (re.compile(r'Pang_Pang_01'), 10),
    (re.compile(r'Pang_Pang_02'), 30),
    (re.compile(r'Pang_Pang_03'), 50),
    (re.compile(r'OrcusGem'), 100),
    (re.compile(r'ThiefShulack'), 30),
]
KILL_ELITE, KILL_NORMAL = 50, 10
# Only these games score kills (the hazards of the others - lasers, tornados, ghosts, answer areas - are not prey).
KILL_GAMES = {'Defence', 'Pang', 'TreasureHunter'}
# The games whose EventFunc phases the server runs over the play time (the others are driven by their map events).
PHASED = {'JumpJump', 'Pang', 'Masquerade', 'TreasureHunter'}
SPAWN_ACTIONS = {'EEventFuncAction::StartSpawner', 'EEventFuncAction::EndSpawner'}
PHASE_ACTIONS = SPAWN_ACTIONS | {'EEventFuncAction::PlayNpcTalk', 'EEventFuncAction::AddAbnormal', 'EEventFuncAction::RemoveAbnormal',
                                 'EEventFuncAction::ShowMessage'}


def rows(tables, name):
    data = json.loads((tables / name).read_text(encoding='utf-8'))
    return data['Properties']['Data'] if 'Properties' in data else data


def enum(value):
    return (value or '').split('::')[-1]


def loc(v):
    return [round(v['X'], 1), round(v['Y'], 1), round(v['Z'], 1)]


def pickup_points(name):
    for pattern, points in PICKUP_POINTS:
        m = pattern.search(name)
        if m:
            return points if points is not None else max(1, int(m.group(1))) * 10
    return 0


def kill_points(name, subtype):
    for pattern, points in KILL_POINTS:
        if pattern.search(name):
            return points
    return KILL_ELITE if subtype == 'EliteMonster' else KILL_NORMAL if subtype == 'NormalMonster' else 0


def build(tables=TABLES):
    events = rows(tables, 'Table_FieldEvent.json')
    env = {r['ID']['Value']: r for r in rows(tables, 'Table_EnvObjData.json')}
    npcs = {r['ID']['Value']: r for r in rows(tables, 'Table_NpcData.json')}
    items = {r['Name']: r['ID']['Value'] for r in rows(tables, 'Table_Item.json')}
    reward_items = {r['Name']: r for r in rows(tables, 'Table_RewardItem.json')}
    cubes = rows(tables, 'Table_RewardDataMonsterCube.json')
    posts = {r['ID']['Value'] for r in rows(tables, 'Table_Post.json')}
    funcs = rows(tables, 'Table_EventFunc.json')
    trap_rounds = rows(tables, 'Table_FieldEventTrapBoard.json')
    boards = {r['Name']: r for r in rows(tables, 'Table_TrapBoard.json')}
    racing = {r['Name']: r for r in rows(tables, 'Table_FieldEventRacing.json')}
    platformer = {r['Name']: r for r in rows(tables, 'Table_FieldEventPlatformer.json')}
    dilemma = rows(tables, 'Table_FieldEventPlatformerDilemma.json')
    masquerade = {r['Name']: r for r in rows(tables, 'Table_FieldEventMasquerade.json')}
    shooting = {r['Name']: r for r in rows(tables, 'Table_FieldEventShooting.json')}
    dungeon = {r['ID']['Value']: r for r in rows(tables, 'Table_Dungeon.json')}
    mapdata = {}
    for path in tables.glob('Map_Dungeon_FieldEvent_*_MapData.json'):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        mapdata[data['MapId']] = (path.name, data)

    def reward_rows(name):
        r = reward_items.get(name)
        if not r:
            return []
        out = []
        for i in r.get('Items') or []:
            if i.get('ItemName') in items:
                out.append([items[i['ItemName']], int(i['ItemProb']), int(i['ItemCountMin']), int(i['ItemCountMax'])])
        return out

    func_groups = {}
    for f in sorted(funcs, key=lambda r: r['ID']['Value']):
        if f['FuncType'] not in PHASE_ACTIONS:
            continue
        func_groups.setdefault(f['GroupName'], []).append(
            [enum(f['FuncType']), f['FuncValue1'], f['FuncValue2'], f['FuncValue3'], f['FuncValue4']])
    group_order = []
    for f in sorted(funcs, key=lambda r: r['ID']['Value']):
        if f['GroupName'] in func_groups and f['GroupName'] not in group_order:
            group_order.append(f['GroupName'])

    out = {}
    for e in events:
        map_id = e['MapId']['Value']
        if map_id not in mapdata:
            continue
        kind = enum(e['FieldEventKind'])
        if kind not in ('ShugoFesta', 'None') or enum(e['EntranceType']) != 'Direct':
            continue
        file_name, data = mapdata[map_id]
        game = re.match(r'Map_Dungeon_FieldEvent_([A-Za-z]+)_', file_name).group(1)
        contents = enum(e['ContentsType'])
        value = e['ContentsValue']
        spawners = {}
        for s in data['SpawnInfoList']:
            spawners[s['Name']] = {
                'npcs': [x['Value'] for x in s['NpcIdList']],
                'envs': [x['Value'] for x in s['EnvObjIdList']],
                'n': len(s['Positions']),
                'tb': s['TrapBoardId']['Value'],
                'at': [loc(p['Location']) for p in s['Positions']],
            }
        pickups = {}
        for s in spawners.values():
            for i in s['envs']:
                row = env.get(i)
                if row and enum(row['Usage']) == 'Overlap':
                    points = pickup_points(row['Name'])
                    if points:
                        pickups[str(i)] = points
        kills = {}
        for s in (spawners.values() if game in KILL_GAMES else []):
            for n in s['npcs']:
                row = npcs.get(n)
                if not row:
                    continue
                points = kill_points(row['Name'], enum(row.get('NpcSubType')))
                if points and enum(row.get('NpcSubType')) in ('NormalMonster', 'EliteMonster'):
                    kills[str(n)] = points
        triggers = {t['Value']['Name']: t['Value'] for t in data.get('TriggerActorDataMap') or []}

        def volume(name):
            t = triggers.get(name)
            if not t:
                return None
            box = t.get('BoxSize') or {'X': 0, 'Y': 0}
            return {'name': name, 'at': loc(t['Location']), 'dir': round(t.get('Direction', 0.0), 3),
                    'shape': enum(t.get('CollisionType')), 'box': [box['X'], box['Y']],
                    'radius': t.get('Radius', 0.0), 'height': t.get('Height', 0.0)}

        entry = {
            'event': e['ID']['Value'], 'name': e['EventName'], 'game': game, 'contents': contents, 'value': value,
            'group': e['EventGroup'], 'progress_s': e['ProgressTime'], 'ready_s': e['ReadyDuration'],
            'hold_s': dungeon.get(map_id, {}).get('CompleteStateHoldingTime', 0),
            'title': e['Title']['Key'], 'cube': e['RewardMonsterCube'],
            'post': 10_000_000 + e['ID']['Value'] if 10_000_000 + e['ID']['Value'] in posts else 0,
            'respawn': [t['Key'] for t in data.get('TeleportDataMap') or []],
            'spawners': spawners, 'pickups': pickups, 'kills': kills,
        }
        # The reward cube by level band.
        rewards = []
        for c in cubes:
            if c['GroupName'] == e['RewardMonsterCube']:
                rewards.append({'min': c['MinLevel'], 'max': c['MaxLevel'], 'common': reward_rows(c['RewardCommon']),
                                'key': reward_rows(c['RewardKey'])})
        entry['rewards'] = sorted(rewards, key=lambda r: r['min'])
        # EventFunc phases of the games without a map event script of their own.
        if game in PHASED:
            on_map = set(spawners)
            picked = []
            for g in group_order:
                acts = func_groups[g]
                targets = [a[1] for a in acts if 'Spawner' in a[0]]
                tokens = g.split('_')
                if not g.startswith('FE_') or 'D' in tokens and map_id < 510000 or 'L' in tokens and map_id >= 510000:
                    continue   # the other race's variant of the group; the G_FE_* toggles of the season-1 script
                if targets and any(t in on_map for t in targets):
                    picked.append(g)
            prefixes = {'_'.join(g.split('_')[:3]) for g in picked}
            phases = []
            for g in group_order:
                acts = func_groups[g]
                targets = [a[1] for a in acts if 'Spawner' in a[0]]
                tokens = g.split('_')
                if not g.startswith('FE_') or 'D' in tokens and map_id < 510000 or 'L' in tokens and map_id >= 510000:
                    continue
                if g in picked or not targets and '_'.join(tokens[:3]) in prefixes:
                    phases.append({'group': g, 'acts': [a for a in acts if 'Spawner' not in a[0] or a[1] in on_map]})
            entry['phases'] = phases
        if contents == 'TrapBoard':
            rounds = []
            for r in sorted((r for r in trap_rounds if r['ContentName'] == value), key=lambda r: r['Round']):
                board = boards.get(r['Board'])
                spawner = next((n for n, s in spawners.items() if board and s['tb'] == board['ID']['Value']), '')
                bonus = next((n for n in spawners if re.search(rf'_BonusEnv{r["Round"]}$', n)), '')
                rounds.append({'id': r['ID']['Value'], 'round': r['Round'], 'board': r['Board'], 'board_id': board['ID']['Value'] if board else 0,
                               'grid': [board['GridX'], board['GridY']] if board else [0, 0], 'spawner': spawner, 'bonus': bonus,
                               'ready_s': r['ReadyTime'], 'ready_count': r['ReadyCount'], 'preview_s': r['PreviewTime'],
                               'answer_s': r['AnswerTime'], 'result_s': r['ResultTime'],
                               'talk': [r['NpcTalkReady'], r['NpcTalkPreview'], r['NpcTalkAnswer'], r['NpcTalkResult']]})
            entry['trapboard'] = {'rounds': rounds, 'right': [int(func_groups['FE_Trapboard_Answer'][0][1]), int(func_groups['FE_Trapboard_Answer'][0][2])],
                                  'wrong': [int(func_groups['FE_Trapboard_Wronganswer'][0][1]), int(func_groups['FE_Trapboard_Wronganswer'][0][2])]}
        if contents == 'Racing' and value in racing:
            r = racing[value]
            laps = sorted((volume(n) for n in triggers if re.search(r'_Lap\d+_Trigger_\d+$', n)), key=lambda v: v['name'])
            entry['racing'] = {'id': r['ID']['Value'], 'laps': r['Lap'], 'start_count': r['StartCount'], 'end_count': r['EndCount'],
                               'track': r['TrackName'], 'triggers': laps}
        if contents == 'Platformer' and value in platformer:
            p = platformer[value]
            entry['platformer'] = {'id': p['ID']['Value'], 'start_wait_s': p['StartWaitSecond'], 'start_countdown_s': p['StartCountdownSecond'],
                                   'end_countdown_s': p['EndCountdownSecond'],
                                   'checkpoints': [volume(n) for n in p['CheckPointList'] if volume(n)]}
        if contents == 'PlatformerDilemma':
            side = '_D_' if map_id >= 510000 else '_L_'
            bonus = {}
            for g, acts in func_groups.items():
                m = re.match(rf'FE_Dilemma{side}(\d+)Round_BonusOd_Spawn$', g)
                if m:
                    bonus[m.group(1)] = [a[1] for a in acts if a[0] == 'StartSpawner']
            holds = {k: int(func_groups[f'FE_Dilemma_{k}_Hold_01'][0][2]) for k in ('Start', 'Ready', 'Result', 'End')}
            right = [int(func_groups[f'FE_Dilemma_Answer_Right_{i:02d}'][0][1]) for i in range(1, 9)]
            entry['dilemma'] = {
                'rounds': [{'id': r['ID']['Value'], 'answer_s': r['AnswerCountdown'], 'talk': [r['ReadyNpcTalk'], r['AnswerNpcTalk'], r['ResultNpcTalk']]}
                           for r in dilemma if r['Name'] == value],
                'boards': sorted(n for n in spawners if re.search(r'_board_\d+$', n)),
                'doubles': sorted(n for n in spawners if re.search(r'_board_\d+_Double$', n)),
                'bonus': bonus, 'holds_ms': holds, 'hold_abnormal': int(func_groups['FE_Dilemma_Start_Hold_01'][0][1]),
                'right': right, 'wrong': int(func_groups['FE_Dilemma_Answer_Wrong_01'][0][1]),
            }
        if contents == 'Masquerade' and value in masquerade:
            entry['masquerade'] = {'id': masquerade[value]['ID']['Value'], 'wait_s': masquerade[value]['WaitTime']}
        if contents == 'Shooting' and value in shooting:
            s = shooting[value]
            entry['shooting'] = {'id': s['ID']['Value'], 'start_countdown_s': s['StartCountdown'], 'end_countdown_s': s['EndCountdown']}
        # Moving platforms: Vessel NPCs and the Vessel waypoint set that starts at their spawn.
        vessels = []
        ways = [w['Value'] for w in data.get('WaypointsDataMap') or [] if enum(w['Value'].get('WaypointType')) == 'Vessel']
        for name, s in spawners.items():
            if not any(enum(npcs.get(n, {}).get('NpcSubType')) == 'Vessel' for n in s['npcs']):
                continue
            for at in s['at']:
                best = min(ways, key=lambda w: sum((w['Points'][0][k] - at[i]) ** 2 for i, k in enumerate('XYZ')), default=None)
                if best and sum((best['Points'][0][k] - at[i]) ** 2 for i, k in enumerate('XYZ')) <= 100 ** 2:
                    vessels.append({'spawner': name, 'route': best['LabelName'], 'points': [loc(p) for p in best['Points']],
                                    'traverse': enum(best.get('TraverseType')), 'speed': best.get('BaseMoveSpeed', 0.0)})
        if vessels:
            entry['vessels'] = vessels
        for s in spawners.values():
            del s['at']
        out[str(map_id)] = entry
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--tables', type=Path, default=TABLES)
    parser.add_argument('--out', type=Path, default=OUT)
    args = parser.parse_args()
    maps = build(args.tables)
    doc = {'source': 'tools/festival_games.py from the client tables (FieldEvent*, TrapBoard, EventFunc, RewardDataMonsterCube, '
                     'RewardItem, Map_Dungeon_FieldEvent_*_MapData); points, phase times and roles CANDIDATE', 'maps': maps}
    args.out.write_text(json.dumps(doc, ensure_ascii=False, separators=(',', ':'), sort_keys=True), encoding='utf-8')
    print(f'{len(maps)} festival maps -> {args.out}')


if __name__ == '__main__':
    main()
