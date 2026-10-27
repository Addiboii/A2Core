"""Monster loot for the server: each NPC's reward tables, their items and packages.

Derived from offline exports (WorldProbe): RewardData, RewardItem, RewardPackage and
RewardPackageItem (research/local/pet-tables-20261016) and Item.dat (research/local/game-catalog-v2).
Rebuild the tracked summary with `python tools/loot_data.py`.

- npcs: NpcData ID (= RewardData ID) -> reward table names (RewardName, BasicRewardName,
  LevelscaleBasicRewardName; the basic table names already carry the NPC's level).
- tables: reward table name -> {race (0 all, 1 Light, 2 Dark), items [[item ID, chance of 1e8,
  count min, count max]], packages [[package, chance of 1e8, count]]}. Every entry is an independent
  roll (CANDIDATE: RewardItem tables often sum above 1e8, e.g. a soul at 1e8 plus a spiritstone).
- packages: RewardPackageItem name -> [[item ID, weight of 1e8, count min, count max]]: one item is
  picked by weight (they sum to 1e8).
Prologue monsters (Poeta, M_L0_*) have no RewardData row and drop nothing.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/loot.json'
TABLES = ROOT / 'research/local/pet-tables-20261016'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
RACES = {'ERace::Light': 1, 'ERace::Dark': 2}


def _rows(index, tables=TABLES):
    return json.loads((tables / f'table-{index:03}.json').read_text(encoding='utf-8'))['Properties']['Data']


def build(tables=TABLES, catalog=CATALOG):
    ids = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    value = lambda v: v['Value'] if isinstance(v, dict) else v
    npcs = {}
    for row in _rows(2, tables):
        names = [row.get('RewardName'), *(row.get('BasicRewardName') or []), *(row.get('LevelscaleBasicRewardName') or [])]
        names = [n for n in dict.fromkeys(names) if n and n != 'None']
        if names:
            npcs[str(value(row['ID']))] = names
    wanted = {n for names in npcs.values() for n in names}
    tables_out = {}
    for row in _rows(1, tables):
        if row['Name'] in wanted:
            items = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1), int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                     for i in row.get('Items') or [] if i.get('ItemName') in ids and int(i.get('ItemProb') or 0) > 0]
            if items:
                tables_out.setdefault(row['Name'], {'race': RACES.get(row.get('Race'), 0), 'items': [], 'packages': []})['items'].extend(items)
    package_names = set()
    for row in _rows(3, tables):
        if row['Name'] in wanted:
            packages = [[p['Item'], int(p.get('Prob') or 0), int(p.get('Count') or 1)] for p in row.get('PackageItems') or [] if int(p.get('Prob') or 0) > 0]
            if packages:
                tables_out.setdefault(row['Name'], {'race': RACES.get(row.get('Race'), 0), 'items': [], 'packages': []})['packages'].extend(packages)
                package_names.update(p[0] for p in packages)
    packages_out = {}
    for row in _rows(4, tables):
        if row['PackageName'] in package_names:
            items = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1), int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                     for i in row.get('Items') or [] if i.get('ItemName') in ids]
            if items:
                packages_out[row['PackageName']] = items
    npcs = {k: [n for n in v if n in tables_out] for k, v in npcs.items()}
    npcs = {k: v for k, v in npcs.items() if v}
    return {'source': 'RewardData/RewardItem/RewardPackage/RewardPackageItem + Item.dat (tools/loot_data.py)',
            'npcs': npcs, 'tables': tables_out, 'packages': packages_out}


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['npcs'])} NPCs, {len(data['tables'])} reward tables, {len(data['packages'])} packages written to {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
