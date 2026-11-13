"""Empyrean Traces and the Monoliths they are resonated with, for the server (GameSession.EmpyreanTraces.cs).

Source: the client table export (tools/dump_tables.ps1 on the gaming PC, research/local/tables-20261109-023531):
- Monolith.dat: the six Monoliths (Verteron 1001, Eltnen 1002, Altgard 2001, Morheim 2002, Reshanta 3001 Light / 4001 Dark):
  race, unlock quest and step, the token item (TokenItem: the "Empyrean Trace: <region>" item a collected trace gives) and the
  achievement groups of its traces (Group_Unlock_MonolithFragment_*: one UseEnvObjSpawnerName achievement per placed trace).
- MonolithLevel.dat: per Monolith the levels 1..30 with NeedTokenCount (the resonance count that reaches the level, cumulative:
  Altgard's level 30 needs 560; OBSERVED MonolithList_NT 0xe30b carries 560 for a finished Altgard) and the RewardMonolith row.
- RewardMonolith.dat: a level's rewards: Stat (EStat + value, a permanent stat) or Item (name, count, enchant level).
- EnvObjData.dat Usage Monolith: the Monolith objects (UsageValue = the Monolith of the object's race, UsageValue2 = the other
  race's in Reshanta) and the traces (bGetRewardOnce objects with a RewardData row reward_manual_Monolith_<id>).
- Item.dat: item ids; UseEffect MonolithAllClear [monolith, map] (the express vouchers).

    python tools/monolith_data.py [--tables DIR]   # writes tools/data/monoliths.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
DATA = ROOT / 'tools/data/monoliths.json'
RACES = {'ERace::Light': 1, 'ERace::Dark': 2}


def rows(tables, name):
    return json.loads((Path(tables) / f'Table_{name}.json').read_text(encoding='utf-8'))['Properties']['Data']


def build(tables=TABLES):
    items = {r['Name']: r['ID']['Value'] for r in rows(tables, 'Item')}
    rewards = {}
    for row in rows(tables, 'RewardMonolith'):
        stats, granted = {}, []
        for entry in row.get('RewardList') or []:
            if entry['Type'].endswith('::Stat') and entry.get('StatType', 'EStat::None') != 'EStat::None':
                stat = entry['StatType'].split('::')[-1]
                stats[stat] = stats.get(stat, 0) + int(entry.get('StatValue') or 0)
            elif entry['Type'].endswith('::Item') and (entry.get('Item') or {}).get('Item') in items:
                item = entry['Item']
                granted.append([items[item['Item']], int(item.get('Count') or 1), int(item.get('EnchantedLevel') or 0)])
        rewards[row['Name']] = (granted, stats)
    levels = {}
    for row in rows(tables, 'MonolithLevel'):
        granted, stats = rewards.get(row.get('Reward'), ([], {}))
        levels.setdefault(row['MonolithName'], []).append([int(row['Level']), int(row['NeedTokenCount']), granted, stats])
    monoliths = {}
    for row in rows(tables, 'Monolith'):
        name = row['Name']
        monoliths[str(row['ID']['Value'])] = {
            'name': name, 'race': RACES.get(row.get('Race'), 0), 'token': items.get(row.get('TokenItem'), 0),
            'unlock_quest': row.get('SyncConditionQuest'), 'unlock_step': (row.get('UnlockQuestStepId') or {}).get('Value', 0),
            'groups': list(row.get('AchievementGroupList') or []),
            'levels': sorted(levels.get(name, []))}
    by_name = {m['name']: int(i) for i, m in monoliths.items()}
    objects = {}
    for row in rows(tables, 'EnvObjData'):
        if row.get('Usage') != 'EEnvObjectUsage::Monolith':
            continue
        ids = [by_name[v] for v in (row.get('UsageValue'), row.get('UsageValue2')) if v in by_name]
        if ids:
            objects[str(row['ID']['Value'])] = ids
    rewarded = {r['ID']['Value'] for r in rows(tables, 'RewardData') if str(r.get('RewardName', '')).startswith('reward_manual_Monolith_')}
    traces = {str(r['ID']['Value']): r['Name'] for r in rows(tables, 'EnvObjData') if r['ID']['Value'] in rewarded and r.get('bGetRewardOnce')}
    vouchers = {}
    for row in rows(tables, 'Item'):
        if row.get('UseEffect') == 'EItemUseEffect::MonolithAllClear' and (row.get('UseEffectValues') or [None])[0] in by_name:
            values = row['UseEffectValues']
            vouchers[str(row['ID']['Value'])] = [by_name[values[0]], int(values[1]) if len(values) > 1 and str(values[1]).isdigit() else 0]
    return {'source': 'Monolith/MonolithLevel/RewardMonolith/EnvObjData/RewardData/Item.dat (tables-20261109-023531, tools/monolith_data.py)',
            'monoliths': monoliths, 'objects': objects, 'traces': traces, 'vouchers': vouchers}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--tables', default=str(TABLES))
    args = parser.parse_args()
    out = build(args.tables)
    DATA.write_text(json.dumps(out, separators=(',', ':')) + '\n', encoding='utf-8')
    print(len(out['monoliths']), 'monoliths,', len(out['objects']), 'monolith objects,', len(out['traces']), 'trace objects,',
          len(out['vouchers']), 'vouchers ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
