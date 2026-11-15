import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import slot_extension_data  # noqa: E402


def _cost(count, name='ECurrencyCategory::GoldCombined'):
    return [{'ItemName': name, 'ItemCount': count}]


class SlotExtensionDataTests(unittest.TestCase):
    def test_costs_lines_and_vouchers(self):
        tables = {
            'InventorySlot': [{'Name': 'None', 'MaxSlotLineCount': 80,
                               'SlotExtensionCostList': [{'ItemName': 'GoldCombined', 'ItemCount': 10000},
                                                         {'ItemName': 'GoldCombined', 'ItemCount': 20000}]}],
            'StorageExtensionCost': [
                {'SlotLineNum': 7, 'SlotExtensionCostList': _cost(1500), 'CharStorageSlotExtensionCostList': _cost(1600)},
                {'SlotLineNum': 1, 'SlotExtensionCostList': _cost(1), 'CharStorageSlotExtensionCostList': _cost(1)},
                {'SlotLineNum': 6, 'SlotExtensionCostList': _cost(1000), 'CharStorageSlotExtensionCostList': _cost(1100)},
                {'SlotLineNum': 8, 'SlotExtensionCostList': _cost(5, 'SomeItem'), 'CharStorageSlotExtensionCostList': _cost(1700)},
            ],
        }
        items = [{'ID': {'Value': 534000001}, 'UseEffect': 'EItemUseEffect::ExtendInventorySlot'},
                 {'ID': {'Value': 536700003}, 'UseEffect': 'EItemUseEffect::ExtendCharacterStorageSlot'},
                 {'ID': {'Value': 1}, 'UseEffect': 'EItemUseEffect::Skill'}]
        settings = {'inventory_default_slot_line_count': 8, 'inventory_slot_count_per_line': 5,
                    'storage_default_slot_line_count': 5, 'storage_max_slot_line_count': 80,
                    'character_storage_default_slot_line_count': 5, 'character_storage_max_slot_line_count': 60}
        out = slot_extension_data.build(tables, items, settings)
        self.assertEqual(out['containers']['1'], {'default_lines': 8, 'max_lines': 80, 'gold_tier': 'gold_extensions',
                                                  'gold_costs': [10000, 20000]})
        # Lines up to the default are skipped; a non-Kina cost ends the gold ladder.
        self.assertEqual(out['containers']['2'], {'default_lines': 5, 'max_lines': 80, 'gold_tier': 'line', 'gold_costs': [1000, 1500]})
        self.assertEqual(out['containers']['15']['gold_costs'], [1100, 1600, 1700])
        self.assertEqual(out['containers']['15']['max_lines'], 60)
        self.assertEqual(out['vouchers'], {'534000001': 1, '536700003': 15})
        self.assertEqual(out['kina_item'], 930100001)


if __name__ == '__main__':
    unittest.main()
