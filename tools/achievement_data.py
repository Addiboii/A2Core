"""Achievements, attendance and the group rewards from the client's own tables.

Sources (research/local/progress-tables-20261018, WorldProbe): Achievement.dat (table-001: id, group, grade, reset cycle,
objective type EAchievementObjectiveType and its values, unlock achievement), AchievementGroup.dat (table-002: id, name,
race, EAchievementCategory), AchievementGroupReward.dat (table-003: group id -> rewards at completion counts),
AchievementReward.dat (table-005: per achievement step the items), AchievementStep.dat (table-006: per step the count),
Attendance.dat (table-000: the check-in boards, each an achievement counting days). Item names resolve through
research/local/game-catalog-v2/entries.jsonl, NPC names through NpcData (tools/map_events.npc_names).

Output tools/data/achievements.json:
  achievements: id -> {g: group name, t: objective type, v: [values], n: [NpcData ids] (KillNpc), i: [item ids]
                       (UseItem/GetItem/AcquireItem), s: [[step, count], ...], r: {step: [[item id, count], ...]},
                       u: unlock achievement id, c: reset cycle (0 none, 1 daily, 2 weekly, 3 monthly, 4 yearly)}
  groups: name -> {i: id, r: race (0 all, 1 light, 2 dark), c: category name}
  group_rewards: group id -> [[complete count, item id, count], ...]
  attendance: id -> {a: achievement id, r: race, d: days, s: season contents type, always: no start date, from/to: window (Unix ms)}
  battlepass: group id -> {n: name, r: race, from/to: activation window, limit: weekly exp limit, passes: [[pass id, exp
              achievement id, premium], ...], missions: the mission achievement group name}; achievements carry p: {step: exp}

    python tools/achievement_data.py build
"""
import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
TABLES = ROOT / 'research/local/progress-tables-20261018'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
DATA = ROOT / 'tools/data/achievements.json'
RACES = {'All': 0, 'Light': 1, 'Dark': 2}
RESETS = {'None': 0, 'Daily': 1, 'Weekly': 2, 'Monthly': 3, 'Yearly': 4}


def _rows(name):
    return json.loads((TABLES / name).read_text(encoding='utf-8'))['Properties']['Data']


def _enum(value):
    return str(value).split('::')[-1]


def _item_ids(catalog=CATALOG):
    ids = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    return ids


def _ticks_ms(ticks):
    """.NET ticks (100 ns since 0001-01-01) -> Unix milliseconds (0 stays 0)."""
    return 0 if not ticks else (ticks - 621355968000000000) // 10000


def _values(raw):
    """ObjValues entries are strings, some a comma-separated list ('A, B, C')."""
    out = []
    for value in raw or []:
        out.extend(v.strip() for v in str(value).split(',') if v.strip())
    return out


ENVOBJS = ROOT / 'research/local/world/gameplay-tables/table-000.json'   # EnvObjData.dat (UseEnvObj names -> ids)


def _envobj_ids(path=ENVOBJS):
    try:
        from world_objects import envobj_rows
        return {row['name']: env for env, row in envobj_rows(json.loads(Path(path).read_text(encoding='utf-8'))).items() if row.get('name')}
    except (OSError, ValueError, ImportError):
        return {}


def build():
    from map_events import npc_names
    ids = _item_ids()
    npcs, _ = npc_names()
    envs = _envobj_ids()
    groups = {row['Name']: {'i': row['ID']['Value'], 'r': RACES.get(_enum(row['Race']), 0), 'c': _enum(row['Category'])} for row in _rows('table-002.json')}
    steps = {}
    for row in _rows('table-006.json'):
        steps.setdefault(row['AchievementId']['Value'], []).append([int(row['Step']), int(row['Count'] or 1)])
    rewards = {}
    points = {}
    for row in _rows('table-005.json'):
        items = [[ids[i['Item']], int(i.get('Count') or 1)] for i in row.get('ItemRewards') or [] if i.get('Item') in ids]
        if items:
            rewards.setdefault(row['AchievementId'], {})[str(row['Step'])] = items
        if int(row.get('RewardPoint') or 0) > 0:
            points.setdefault(row['AchievementId'], {})[str(row['Step'])] = int(row['RewardPoint'])
    achievements = {}
    for row in _rows('table-001.json'):
        aid = row['AchievementId']['Value']
        kind = _enum(row['ObjType'])
        values = _values(row.get('ObjValues'))
        entry = {'g': row['GroupName'], 't': kind, 'v': values, 's': sorted(steps.get(aid, [[1, 1]])), 'u': row['UnlockAchievementId']['Value'],
                 'c': RESETS.get(_enum(row['ResetCycleType']), 0)}
        if kind in ('KillNpc', 'KillNpcByCritical', 'CloseNPC'):
            entry['n'] = [npcs[v] for v in values if v in npcs]
        if kind in ('UseItem', 'GetItem', 'AcquireItem'):
            entry['i'] = [ids[v] for v in values if v in ids]
        if kind in ('UseEnvObj', 'CloseEnvObj', 'UseEnvObjWithKey'):
            entry['e'] = [envs[v] for v in values if v in envs]
        if aid in rewards:
            entry['r'] = rewards[aid]
        if aid in points:
            entry['p'] = points[aid]   # battle pass exp per step (AchievementReward.RewardPoint)
        achievements[str(aid)] = entry
    group_rewards = {}
    for row in _rows('table-003.json'):
        items = [[int(r['CompleteCount']), ids[r['RewardItem']['Item']], int(r['RewardItem'].get('Count') or 1)]
                 for r in row.get('ItemRewards') or [] if r.get('RewardItem', {}).get('Item') in ids]
        if items:
            group_rewards[str(row['GroupId']['Value'])] = items
    attendance = {}
    for row in _rows('table-000.json'):
        start = int((row.get('AttendanceStartDate') or {}).get('Ticks') or 0)
        end = int((row.get('AttendanceEndDate') or {}).get('Ticks') or 0)
        attendance[str(row['ID']['Value'])] = {'a': row['AchievementId']['Value'], 'r': RACES.get(_enum(row['Race']), 0), 'd': int(row['AttendanceMaxDays'] or 0),
                                               's': _enum(row['SeasonContentsType']), 'always': start == 0,
                                               'from': _ticks_ms(start), 'to': _ticks_ms(end)}
    passes = {}
    for row in _rows('table-007.json'):
        passes.setdefault(row['GroupName'], []).append([row['ID']['Value'], row['ExpAchievementId']['Value'], int(int(row.get('UnlockCostCount') or 0) > 0 or bool(row.get('bIsPremium')))])
    battlepass = {}
    for row in _rows('table-008.json'):
        name = row['Name']
        missions = name.replace('battlepass', 'BattlePass_Mission').replace('Eventpass', 'EventPass_Mission')
        battlepass[str(row['ID']['Value'])] = {'n': name, 'r': RACES.get(_enum(row['Race']), 0), 'from': _ticks_ms(int((row.get('ActivationStartDate') or {}).get('Ticks') or 0)),
                                               'to': _ticks_ms(int((row.get('ActivationEndDate') or {}).get('Ticks') or 0)), 'limit': int(row.get('ExpLimitValue') or 0),
                                               'passes': passes.get(name, []), 'missions': missions if missions in groups else None}
    return {'source': 'Achievement/AchievementGroup/AchievementGroupReward/AchievementReward/AchievementStep/Attendance/BattlePass/BattlePassGroup.dat (tools/achievement_data.py)',
            'achievements': achievements, 'groups': groups, 'group_rewards': group_rewards, 'attendance': attendance, 'battlepass': battlepass}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['achievements'])} achievements, {len(data['groups'])} groups, {len(data['group_rewards'])} group reward sets, "
          f"{len(data['attendance'])} attendance boards -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
