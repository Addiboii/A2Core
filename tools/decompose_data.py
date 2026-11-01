"""Item dismantling (ItemDecompose) data from the client's own tables.

Sources (offline exports, WorldProbe):
- Item.dat (research/local/quest-tables/table-016.json): CanDecompose, DecomposeCostGold (kina per item) and
  DecomposeResultItemGroup (two RewardDecomposePackage group names: the grade's and the level's).
- RewardDecomposePackage.dat (research/local/post-tables-20261018/table-000.json): group Name + EnchantLevel (the
  row whose EnchantLevel is the highest at or below the item's enchant applies) -> Packages [{Item: RewardPackageItem
  name, Prob of 1e8, Count}].
- RewardPackageItem.dat (research/local/quest-tables/table-014.json): package -> Items [{ItemName, ItemProb, min, max}]
  (one item picked by weight, as tools/loot_data.py reads packages).
- research/local/game-catalog-v2/entries.jsonl: item internal names -> ids.

    python tools/decompose_data.py build    # writes tools/data/decompose.json
"""
import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json
import extraction_skin_rows as esr
ITEMS = ROOT / 'research/local/quest-tables/table-016.json'
PACKAGE_ITEMS = ROOT / 'research/local/quest-tables/table-014.json'
GROUPS = ROOT / 'research/local/post-tables-20261018/table-000.json'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
DATA = ROOT / 'tools/data/decompose.json'
SKINS = ROOT / 'tools/data/skins.json'


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def build(items=ITEMS, package_items=PACKAGE_ITEMS, groups=GROUPS, catalog=CATALOG, skins=SKINS):
    ids = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    skin_map = json.loads(skins.read_text(encoding='utf-8')).get('skins', {}) if skins.exists() else {}
    out_items = {}
    for row in _rows(items):
        if not row.get('CanDecompose') or not isinstance(row.get('ID'), dict):
            continue
        names = [g for g in row.get('DecomposeResultItemGroup') or [] if g and g != 'None']
        if names:
            grade = None
            for g in names:
                if m := re.match(r'De_Package_(\w+)_', g):
                    if m.group(1) not in ('Level',):
                        grade = m.group(1)
                        break
            row_out = {'c': int(row.get('DecomposeCostGold') or 0), 'g': names}
            if grade:
                row_out['gr'] = grade
            skin_names = row.get('DecomposeSkins') or row.get('ExtractionSkins') or row.get('SkinExtractionSkins')
            if isinstance(skin_names, list):
                skin_ids = esr.skin_ids(skin_names, skin_map)
                if skin_ids:
                    row_out['s'] = skin_ids[0]
            out_items[str(row['ID']['Value'])] = row_out
    wanted_groups = {g for item in out_items.values() for g in item['g']}
    out_groups = {}
    wanted_packages = set()
    for row in _rows(groups):
        if row.get('Name') not in wanted_groups:
            continue
        packages = [{'n': p['Item'], 'p': int(p.get('Prob') or 0), 'c': int(p.get('Count') or 1)} for p in row.get('Packages') or [] if p.get('Item')]
        out_groups.setdefault(row['Name'], []).append({'e': int(row.get('EnchantLevel') or 0), 'p': packages})
        wanted_packages.update(p['n'] for p in packages)
    for rows in out_groups.values():
        rows.sort(key=lambda r: r['e'])
    out_packages = {}
    for row in _rows(package_items):
        if row.get('PackageName') in wanted_packages:
            entries = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1), int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                       for i in row.get('Items') or [] if i.get('ItemName') in ids and int(i.get('ItemProb') or 0) > 0]
            if entries:
                out_packages.setdefault(row['PackageName'], []).extend(entries)
    return {'source': 'Item.dat CanDecompose/DecomposeCostGold/DecomposeResultItemGroup + RewardDecomposePackage.dat + RewardPackageItem.dat (tools/decompose_data.py)',
            'items': out_items, 'groups': out_groups, 'packages': out_packages}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    data = build()
    write_data_json(DATA, data)
    print(f"{len(data['items'])} dismantlable items, {len(data['groups'])} groups, {len(data['packages'])} packages -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
