"""Faction (race) and class rows of the reward boxes and reward packages, and the Light/Dark twins of items.

The client tables give some box groups one row per race (BoxReward Race Light / Dark, 67 groups: field boss chests,
gathering skin chests, the Founders chests, the class equipment chests), the class equipment chests one row per class
(BoxClassReward Class + Race) and four RewardPackage names one row per race (the treasure chests
reward_manual_TreasureBox_L/D_Normal/Key). tools/box_data.py and tools/loot_data.py key their summaries by name, so
they kept one race's row (boxes.json: the Dark row) or merged both (loot.json: Light and Dark packages in one table).
This file keeps the rows apart; the server picks the opener's row (RewardBoxes / LootTables / FactionRewards).

Rebuild the tracked summary with `python tools/box_race_data.py [client table export folder]` (default
research/local/tables-20261109-023531: Table_BoxReward / Table_BoxClassReward / Table_RewardPackage /
Table_RewardPackageItem / Table_Item .json from the client). Item names map to ids through gamedata/items/*.xml.

- boxes: BoxReward Group -> {"1": Light row, "2": Dark row}, each {items, select, packages, max} as boxes.json.
- class_boxes: BoxClassReward Group -> race ("0" All, "1", "2") -> ECharacterClass name -> {items, select, packages}.
- loot_packages: RewardPackage Name -> {"1": [[package, chance of 1e8, count]], "2": [...]} (the race rows only).
- packages: RewardPackageItem name -> [[item ID, ItemProb, count min, count max]] for every package named above.
- twins: item ID -> the item of the other faction with the same internal name but the race token swapped
  (_L_ <-> _D_, _l_ <-> _d_: Wing_L_Gather_01a_f <-> Wing_D_Gather_01a_f, Title_L_u_* <-> Title_D_u_*), only when
  exactly one such item of the other race exists (2,544 of the 2,709 Light/Dark items; house decorations have none).
"""
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/box-race.json'
TABLES = ROOT / 'research/local/tables-20261109-023531'
ITEMS = ROOT / 'gamedata/items'
RACES = {'ERace::Light': '1', 'ERace::Dark': '2'}
OTHER = {'Light': 'Dark', 'Dark': 'Light'}
TOKENS = {'Light': ('L', 'l'), 'Dark': ('D', 'd')}


def _rows(tables, name):
    return json.loads((tables / f'Table_{name}.json').read_text(encoding='utf-8'))['Properties']['Data']


def _items(folder=ITEMS):
    """internal name -> (id, race) from gamedata/items/*.xml."""
    found = {}
    for path in sorted(folder.glob('*.xml')):
        for item in ET.parse(path).getroot():
            found[item.findtext('internal_name')] = (int(item.findtext('id')), item.findtext('race') or 'All')
    return found


def twins(items):
    out = {}
    for name, (item_id, race) in items.items():
        if race not in OTHER:
            continue
        parts = re.split(r'(_)', name)
        found = set()
        for index, part in enumerate(parts):
            for mine, theirs in zip(TOKENS[race], TOKENS[OTHER[race]]):
                if part == mine:
                    swapped = ''.join(parts[:index] + [theirs] + parts[index + 1:])
                    if swapped in items and items[swapped][1] == OTHER[race]:
                        found.add(items[swapped][0])
        if len(found) == 1:
            out[str(item_id)] = found.pop()
    return dict(sorted(out.items(), key=lambda kv: int(kv[0])))


def build(tables=TABLES, folder=ITEMS):
    items = _items(folder)
    ids = {name: value[0] for name, value in items.items()}

    def listed(rows):
        return [[ids[r['Item']], int(r.get('Count') or 1)] for r in rows or [] if r.get('Item') in ids]

    def box(row, with_max=True):
        out = {'items': listed(row.get('Items')), 'select': listed(row.get('SelectItems')),
               'packages': [[p['Item'], int(p.get('Prob') or 0), int(p.get('Count') or 1)] for p in row.get('PackageItems') or []]}
        if with_max:
            out['max'] = max(1, int(row.get('MultiUseMax') or 1))
        return out

    boxes = {}
    for row in _rows(tables, 'BoxReward'):
        if row.get('Race') in RACES:
            boxes.setdefault(row['Group'], {})[RACES[row['Race']]] = box(row)
    class_boxes = {}
    for row in _rows(tables, 'BoxClassReward'):
        race = RACES.get(row.get('Race'), '0')
        cls = str(row.get('Class', '')).split('::')[-1]
        class_boxes.setdefault(row['Group'], {}).setdefault(race, {})[cls] = box(row, with_max=False)
    loot_packages = {}
    for row in _rows(tables, 'RewardPackage'):
        if row.get('Race') in RACES:
            loot_packages.setdefault(row['Name'], {})[RACES[row['Race']]] = [
                [p['Item'], int(p.get('Prob') or 0), int(p.get('Count') or 1)] for p in row.get('PackageItems') or [] if int(p.get('Prob') or 0) > 0]
    wanted = {p[0] for rows in boxes.values() for b in rows.values() for p in b['packages']}
    wanted |= {p[0] for races in class_boxes.values() for classes in races.values() for b in classes.values() for p in b['packages']}
    wanted |= {p[0] for races in loot_packages.values() for rows in races.values() for p in rows}
    packages = {}
    for row in _rows(tables, 'RewardPackageItem'):
        if row['PackageName'] in wanted:
            packages[row['PackageName']] = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1),
                                             int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                                            for i in row.get('Items') or [] if i.get('ItemName') in ids]
    return {'source': 'BoxReward/BoxClassReward/RewardPackage/RewardPackageItem race and class rows + gamedata/items (tools/box_race_data.py)',
            'boxes': boxes, 'class_boxes': class_boxes, 'loot_packages': loot_packages, 'packages': packages, 'twins': twins(items)}


def main():
    tables = Path(sys.argv[1]) if len(sys.argv) > 1 else TABLES
    data = build(tables)
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['boxes'])} race boxes, {len(data['class_boxes'])} class boxes, {len(data['loot_packages'])} race reward packages, "
          f"{len(data['packages'])} packages, {len(data['twins'])} twins written to {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
