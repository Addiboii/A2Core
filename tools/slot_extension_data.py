"""Bag and warehouse slot extensions -> tools/data/slot-extensions.json.

Sources (OBSERVED field names and values):
- InventorySlot.dat and StorageExtensionCost.dat, Steam build 26262663, extracted with tools/WorldProbe (selection
  research/local/slot-extension-selection.json) into research/local/slot-extension-tables-26262663.
  InventorySlot: MaxSlotLineCount 80 and SlotExtensionCostList, ten GoldCombined costs (the one decoded row).
  StorageExtensionCost: per SlotLineNum (1-36) SlotExtensionCostList (kStorage) and CharStorageSlotExtensionCostList
  (kCharacterStorage), all GoldCombined.
- Item.dat (26262663, research/local/reuse-table-probe): the vouchers, UseEffect ExtendInventorySlot / ExtendStorageSlot /
  ExtendCharacterStorageSlot ("Unlocks 1 line of ...").
- GlobalSetting.dat (2.0.6, research/local/ascension-flight-tables/table-003.json; KEPT-FROM-2.0.6, as tools/economy_data.py):
  inventory/storage/character_storage default line counts, storage/character storage max line counts, slots per line.

CANDIDATE: one extension (gold or voucher) opens one line (the voucher texts say so); the bag's gold extension n costs
InventorySlot cost n; a warehouse's gold extension costs the StorageExtensionCost row of the line it opens
(default + extensions so far + 1; rows 1-5 cost 1 Kina, the default lines). 'gold_tier' says which count picks the
cost: 'gold_extensions' (the bag: gold extensions so far) or 'line' (gold and voucher extensions so far). GoldCombined is Kina (item 930100001, as
tools/shop_data.py).

    python tools/slot_extension_data.py build
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/slot-extensions.json'
TABLES = ROOT / 'research/local/slot-extension-tables-26262663'
ITEMS = ROOT / 'research/local/reuse-table-probe'
SETTINGS = ROOT / 'research/local/ascension-flight-tables/table-003.json'
KINA = 930100001
# EItemContainerType (usmap): kInventory 1, kStorage 2, kCharacterStorage 15.
INVENTORY, STORAGE, CHARACTER_STORAGE = 1, 2, 15
VOUCHER_EFFECTS = {'ExtendInventorySlot': INVENTORY, 'ExtendStorageSlot': STORAGE, 'ExtendCharacterStorageSlot': CHARACTER_STORAGE}


def _enum(value):
    return str(value or '').split('::')[-1]


def _rows(table):
    props = table['Properties']
    if isinstance(props, dict):
        props = props.get('Data', next(iter(props.values())))
    return props


def load_folder(folder, names):
    """{table name: rows} for the named tables of a WorldProbe output folder (summary.json)."""
    folder = Path(folder)
    summary = json.loads((folder / 'summary.json').read_text(encoding='utf-8'))
    out = {}
    for package in summary['packages']:
        name = Path(package['path']).stem
        if name in names and package.get('file'):
            out[name] = _rows(json.loads((folder / package['file']).read_text(encoding='utf-8')))
    return out


def load_settings(path=SETTINGS):
    data = json.loads(Path(path).read_text(encoding='utf-8'))
    rows = _rows(data) if isinstance(data, dict) else data
    return rows[0]


def _gold(costs):
    """A cost list -> Kina, or None when it names anything but GoldCombined."""
    total = 0
    for c in costs or []:
        if _enum(c.get('ItemName')) != 'GoldCombined':
            return None
        total += int(c.get('ItemCount') or 0)
    return total


def build(tables, items, settings):
    inventory_row = tables['InventorySlot'][0]
    storage_rows = sorted(tables['StorageExtensionCost'], key=lambda r: int(r['SlotLineNum']))
    vouchers = {}
    for row in items:
        container = VOUCHER_EFFECTS.get(_enum(row.get('UseEffect')))
        if container:
            item_id = row['ID']['Value'] if isinstance(row.get('ID'), dict) else row.get('ID')
            vouchers[str(int(item_id))] = container

    def storage(field, default, maximum):
        costs = []
        for r in storage_rows:
            if int(r['SlotLineNum']) <= default:
                continue
            kina = _gold(r.get(field))
            if kina is None:
                break
            costs.append(kina)
        return {'default_lines': default, 'max_lines': maximum, 'gold_tier': 'line', 'gold_costs': costs}

    inventory_costs = [_gold([c]) for c in inventory_row.get('SlotExtensionCostList') or []]
    containers = {
        str(INVENTORY): {'default_lines': int(settings['inventory_default_slot_line_count']),
                         'max_lines': int(inventory_row['MaxSlotLineCount']), 'gold_tier': 'gold_extensions',
                         'gold_costs': [c for c in inventory_costs if c is not None]},
        str(STORAGE): storage('SlotExtensionCostList', int(settings['storage_default_slot_line_count']),
                              int(settings['storage_max_slot_line_count'])),
        str(CHARACTER_STORAGE): storage('CharStorageSlotExtensionCostList', int(settings['character_storage_default_slot_line_count']),
                                        int(settings['character_storage_max_slot_line_count'])),
    }
    return {'source': 'InventorySlot + StorageExtensionCost + Item.dat UseEffect (26262663); line counts GlobalSetting 2.0.6 '
                      '(tools/slot_extension_data.py)',
            'kina_item': KINA,
            'slots_per_line': int(settings['inventory_slot_count_per_line']),
            'containers': containers,
            'vouchers': dict(sorted(vouchers.items()))}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', default=str(TABLES))
    parser.add_argument('--items', default=str(ITEMS))
    parser.add_argument('--settings', default=str(SETTINGS))
    args = parser.parse_args()
    tables = load_folder(args.tables, {'InventorySlot', 'StorageExtensionCost'})
    items = load_folder(args.items, {'Item'})['Item']
    out = build(tables, items, load_settings(args.settings))
    DATA.write_text(json.dumps(out, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    print({k: len(v['gold_costs']) for k, v in out['containers'].items()}, 'gold tiers,', len(out['vouchers']), 'vouchers ->',
          DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
