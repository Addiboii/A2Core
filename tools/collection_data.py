"""Period item collections (the season's collection book) from the client's own tables.

Sources (research/local/progress-tables-20261018, WorldProbe): PeriodItemCollection.dat (table-009: the collections per
race and tab; a dated collection's id encodes the season week, the tab kind and the day: 54|13|1|7 = Light, week 13,
daily, day 7 — OBSERVED as the collection the official listed on 2026-10-13; 54|13|2|1 the week's weekly one; the
Craft (…|3|1) and Boss (…|4|1) collections run the whole season), PeriodItemCollectionList.dat (table-010: the lists
of a collection group: target items, the count to register, the rewards). Item names resolve through
research/local/game-catalog-v2/entries.jsonl.

The season window is OBSERVED, not tabled: the Craft/Boss collections ran 2026-07-15 07:00 UTC → 2027-01-20 07:00 UTC,
the daily/weekly windows reset at 07:00 UTC, week 1 starting 2026-07-15 (the data has 22 weeks of dated collections).

    python tools/collection_data.py build    # writes tools/data/collections.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/progress-tables-20261018'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
DATA = ROOT / 'tools/data/collections.json'
RACES = {'All': 0, 'Light': 1, 'Dark': 2}
SEASON_START_MS = 1784098800000   # 2026-07-15 07:00:00 UTC
SEASON_END_MS = 1800342000000     # 2027-01-20 07:00:00 UTC


def _rows(name):
    return json.loads((TABLES / name).read_text(encoding='utf-8'))['Properties']['Data']


def build():
    ids = {}
    with open(CATALOG, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    collections = {}
    for row in _rows('table-009.json'):
        cid = row['ID']['Value']
        collections[str(cid)] = {'g': row['GroupName'], 'r': RACES.get(str(row['Race']).split('::')[-1], 0),
                                 't': int(str(row['TabType']).split('_')[-1]) if 'Tab_' in str(row['TabType']) else 0,
                                 'dated': bool(row.get('bShowDate')), 'week': (cid // 100) % 100, 'kind': (cid // 10) % 10, 'day': cid % 10,
                                 'level': int(row.get('LevelMin') or 1)}
    lists = {}
    for row in _rows('table-010.json'):
        targets = [ids[i['Item']] for i in row.get('TargetItems') or [] if i.get('Item') in ids]
        rewards = [[ids[i['Item']], int(i.get('Count') or 1)] for i in row.get('RewardItems') or [] if i.get('Item') in ids]
        if targets:
            lists.setdefault(row['GroupName'], {})[str(row['ID']['Value'])] = {'n': int(row.get('TargetCount') or 1), 'targets': targets, 'rewards': rewards,
                                                                             'exp': int(row.get('RewardExp') or 0)}
    return {'source': 'PeriodItemCollection/PeriodItemCollectionList.dat (tools/collection_data.py); season window OBSERVED',
            'season': {'from': SEASON_START_MS, 'to': SEASON_END_MS, 'reset_hour_utc': 7, 'daily_lists': 10, 'weekly_lists': 10},
            'collections': collections, 'lists': lists}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['collections'])} collections, {sum(len(v) for v in data['lists'].values())} lists in {len(data['lists'])} groups -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
