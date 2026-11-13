"""Duty missions and duty scrolls (the 783 daily/weekly "duty" quests) from the client tables.

Source (client table export, default research/local/tables-20261109-023531):
- Quest.dat: Type DutyMission (206, AcquireType None: the mission board) and DutyScroll (577, AcquireType Item: the scrolls),
  race, UnlockLevel, Grade, RepeatType.
- QuestType.dat: per type GetLimitCount (20, shared counting group 6), CompleteLimitCount (DutyMission 5 a day, owner Server),
  bCanDuplicate, bCanReroll, bUseRandomReward.
- GlobalSetting: dutymission_quest_journal_acquired_count (15 missions on the board), the reset time (16:00 KST = 07:00 UTC).
- DutyQuestRedrawCost.dat: re-draw prices per type (Kina, GoldCombined), RedrawMax 10.
- DutyQuestGroup.dat: the 44 scroll groups (ID, Name, DutyCost only: the client has no quest list per group).
- Item.dat: UseEffect GetDutyQuest [group name] (86 scroll items).
- QuestReward.dat RandomReward -> RewardPackage.dat (5 slots, each a package name with Prob of 100,000,000) ->
  RewardPackageItem.dat (the slot's candidates: item, ItemProb weight, count min/max, enchant).
  OBSERVED (official DutyMissionQuestJournalStatus_NT, rec 20261106-115817 #173793): every board mission carries 5 items, one per
  slot, each a candidate of that slot's package, with the slot's Prob (100M, 50M, 33M, 25M, 10M) as _prob (19 of 19 missions).
- tools/data/quests.json: each quest's objective maps (the scroll groups below are chosen by them).

CANDIDATE (not in the client data): which quests a scroll group draws. The rule (by race L/D, map, grade, id block):
  L1_Verteron / D1_Altgard -> the level-45 DutyScroll quests of map 1010 / 1110; L2_Eltnen / D2_Morheim -> map 1011 / 1111;
  Event_ChristMas_<grade> -> the level-10 quests of map 1010 / 1110 of that grade; Abyss_<grade>_NN -> map 20 (Lower Reshanta,
  "Latesran ... Root Command") of that grade, NN 01 the ids x309010-049, 02 x309050-089; Abyss_B_Rare_NN -> map 21 (Middle) in
  the same blocks; Abyss_C_<grade>_01/02 -> map 22 KillNpc quests (01 x309090-131 + x309174-177, 02 x309132-173 + x309178-181);
  Abyss_C_Rare_03/04 -> the map 22 player-kill quests ("Player Kill Mission": KillPC / KillPCAbyssGrade, 03 x309182-185,
  04 x309186-189, every grade). Every DutyScroll quest lands in a group; the _01 and _02 groups of
  Verteron / Altgard draw the same quests (the scroll items name only _02).

    python tools/duty_data.py build [--tables DIR]     # writes tools/data/duty.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/duty.json'
QUESTS = ROOT / 'tools/data/quests.json'
TABLES = ROOT / 'research/local/tables-20261109-023531'
GRADES = ('Common', 'Rare', 'Unique', 'Legend')


def _enum(value):
    return value.split('::', 1)[1] if isinstance(value, str) and '::' in value else value


def _rows(tables, name):
    return json.loads((tables / f'Table_{name}.json').read_text(encoding='utf-8'))['Properties']['Data']


def _id(value):
    return value.get('Value') if isinstance(value, dict) else value


def _block(group, number):
    """The id block (quest id % 1000) a group index draws (CANDIDATE, see the module text)."""
    if group.startswith('Abyss_C_'):
        return {1: [(90, 131), (174, 177)], 2: [(132, 173), (178, 181)], 3: [(182, 185)], 4: [(186, 189)]}.get(number, [])
    if group.startswith('Abyss_'):
        return {1: [(10, 49)], 2: [(50, 89)]}.get(number, [])
    return [(0, 999)]


def group_pool(name, scrolls):
    """The DutyScroll quest codes a group draws (CANDIDATE rule)."""
    parts = name.split('_')[1:]            # DutyScroll_L1_Abyss_C_Rare_03 -> [L1, Abyss, C, Rare, 03]
    race = 'Light' if parts[0].startswith('L') else 'Dark'
    prefix = '1' if race == 'Light' else '2'
    number = int(parts[-1])
    rest = parts[1:-1]
    grade = next((g for g in GRADES if g in rest), None)
    out = []
    for q in scrolls:
        if not str(q['id']).startswith(prefix):
            continue
        maps = {o.get('map') for s in q['steps'] for o in s['objectives']}
        kinds = {o['type'] for s in q['steps'] for o in s['objectives']}
        player_kill = bool(kinds & {'KillPC', 'KillPCAbyssGrade'})
        suffix = q['id'] % 1000
        if rest[0] in ('Verteron', 'Altgard', 'Eltnen', 'Morheim'):
            want = {'Verteron': 1010, 'Altgard': 1110, 'Eltnen': 1011, 'Morheim': 1111}[rest[0]]
            ok = maps == {want} and q['level'] > 10
        elif rest[0] == 'Event':
            ok = maps <= {1010, 1110} and q['level'] == 10 and q['grade'] == grade
        elif rest[0] == 'Abyss':
            zone = rest[1] if len(rest) > 2 else ''
            want = {'': 20, 'B': 21, 'C': 22}[zone]
            blocks = _block('Abyss_C_' if zone == 'C' else 'Abyss_', number)
            in_block = any(a <= suffix <= b for a, b in blocks)
            if zone == 'C' and number >= 3:
                ok = maps == {22} and player_kill and in_block
            else:
                ok = maps == {want} and not player_kill and q['grade'] == grade and in_block
        else:
            ok = False
        if ok:
            out.append(q['code'])
    return sorted(out)


def build(tables):
    quests_file = json.loads(QUESTS.read_text(encoding='utf-8'))['quests']
    raw = {_id(r['ID']): r for r in _rows(tables, 'Quest')}
    items = {r['Name']: _id(r['ID']) for r in _rows(tables, 'Item')}
    duty = []
    for code, q in quests_file.items():
        r = raw.get(q['id'])
        if r is None or _enum(r['Type']) not in ('DutyMission', 'DutyScroll'):
            continue
        duty.append({'code': code, 'id': q['id'], 'type': _enum(r['Type']), 'race': _enum(r['Race']), 'level': r['UnlockLevel'],
                     'grade': _enum(r['Grade']), 'repeat': _enum(r['RepeatType']), 'steps': q['steps'],
                     'random': (q.get('rewards') or {}).get('random')})
    scrolls = [q for q in duty if q['type'] == 'DutyScroll']
    missions = sorted([q for q in duty if q['type'] == 'DutyMission'], key=lambda q: q['id'])

    groups = {}
    for g in _rows(tables, 'DutyQuestGroup'):
        groups[g['Name']] = {'id': _id(g['ID']), 'cost': g['DutyCost'], 'quests': group_pool(g['Name'], scrolls)}
    grouped = [c for g in groups.values() for c in g['quests']]
    shared = sorted({c for c in grouped if grouped.count(c) > 1})
    ungrouped = sorted({q['code'] for q in scrolls} - set(grouped))

    redraw = {}
    for r in _rows(tables, 'DutyQuestRedrawCost'):
        costs = [[items[c['Item']], c['Count']] for c in r['CostList']]
        redraw[_enum(r['QuestType'])] = {'name': r['Name'], 'max': r['RedrawMax'], 'costs': costs}

    types = {}
    for r in _rows(tables, 'QuestType'):
        t = _enum(r['Type'])
        if t in ('DutyMission', 'DutyScroll'):
            types[t] = {'get_limit': r['GetLimitCount'], 'limit_group': r['GetLimitCountingGroupId'], 'complete_limit': r['CompleteLimitCount'],
                        'complete_owner': _enum(r['CompleteCountOwnerType']), 'duplicate': r['bCanDuplicate'], 'reroll': r['bCanReroll'],
                        'random_reward': r['bUseRandomReward']}

    setting = _rows(tables, 'GlobalSetting')
    setting = setting[0] if isinstance(setting, list) else setting
    reset_ms = setting['weekly_quest_reset_time']['Ticks'] // 10_000

    scroll_items = {}
    for r in _rows(tables, 'Item'):
        if _enum(r.get('UseEffect')) == 'GetDutyQuest' and r.get('UseEffectValues'):
            scroll_items[str(_id(r['ID']))] = r['UseEffectValues'][0]

    packages = {r['Name']: r for r in _rows(tables, 'RewardPackage')}
    slot_rows = {}
    for r in _rows(tables, 'RewardPackageItem'):
        slot_rows.setdefault(r['PackageName'], []).extend(r['Items'])
    used = sorted({q['random'] for q in duty if q['random']})
    out_packages, out_slots, unresolved = {}, {}, set()
    for name in used:
        pkg = packages[name]
        out_packages[name] = [[s['Item'], s['Prob'], s['Count']] for s in pkg['PackageItems']]
        for s in pkg['PackageItems']:
            if s['Item'] in out_slots:
                continue
            entries = []
            for i in slot_rows.get(s['Item'], []):
                item = items.get(i['ItemName'])
                if item is None:
                    unresolved.add(i['ItemName'])
                    continue
                entries.append([item, i['ItemProb'], i['ItemCountMin'], i['ItemCountMax'], i['ItemEnchant']])
            out_slots[s['Item']] = entries

    return {
        'source': {'tables': tables.name, 'missions': len(missions), 'scrolls': len(scrolls), 'ungrouped_scroll_quests': ungrouped, 'quests_in_two_groups': len(shared),
                   'unresolved_reward_items': sorted(unresolved)},
        'board_size': setting['dutymission_quest_journal_acquired_count'],
        'reset_kst_ms': reset_ms,
        'weekly_reset_day': setting['weekly_quest_reset_day'],
        'types': types,
        'redraw': redraw,
        'missions': [{'code': q['code'], 'race': q['race'], 'level': q['level'], 'grade': q['grade']} for q in missions],
        'groups': groups,
        'scroll_items': scroll_items,
        'packages': out_packages,
        'slots': out_slots,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', type=Path, default=TABLES)
    args = parser.parse_args()
    data = build(args.tables)
    DATA.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    print('wrote %s: %d missions, %d scroll quests (%d without a group), %d groups, %d scroll items, %d packages, %d slots, %d bytes'
          % (DATA, data['source']['missions'], data['source']['scrolls'], len(data['source']['ungrouped_scroll_quests']),
             len(data['groups']), len(data['scroll_items']), len(data['packages']), len(data['slots']), DATA.stat().st_size))
    print('group sizes:', {k: len(v['quests']) for k, v in data['groups'].items()})
    if data['source']['unresolved_reward_items']:
        print('unresolved reward items:', data['source']['unresolved_reward_items'])


if __name__ == '__main__':
    main()
