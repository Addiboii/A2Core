import json
import unittest
from pathlib import Path

DATA = json.loads((Path(__file__).resolve().parents[1] / 'tools/data/manastones.json').read_text(encoding='utf-8'))


class ManastoneTests(unittest.TestCase):
    def test_recorded_stone_and_item(self):
        # Community recording 2026-11-06 234912 i49416/49417: stone 531360001 on the Magicbook 110550046, two slots,
        # 1000 Kinah per imprint; 2026-11-04 163050: stones 531351001 and 531360003 on a four-slot item, 2000 Kinah.
        self.assertEqual(DATA['stones']['531360001'], ['ms_10_common_normal', 'Magicstone_Group_Equip'])
        self.assertEqual(DATA['stones']['531351001'][0], 'ms_10_rare_abyss')
        self.assertEqual(DATA['items']['110550046'], [2, 1000, 'GoldCombined', ['Magicstone_Group_Equip']])

    def test_recorded_rolls_are_in_the_stones_group(self):
        group = {s[0]: s for s in DATA['groups']['ms_10_common_normal']}
        for stat, grade in [(199, 2), (307, 1), (255, 1), (199, 1), (193, 2), (128, 1), (133, 2), (104, 1)]:
            self.assertIn(stat, group)
            self.assertIn(grade, [g for g, _, _ in group[stat][3]])
        self.assertEqual(group[199][1], 'MPMax')
        self.assertEqual(group[307][1], 'ArmorDefense')

    def test_grade_chances_are_out_of_ten_thousand(self):
        for name, stats in DATA['groups'].items():
            for stat in stats:
                self.assertEqual(sum(p for _, p, _ in stat[3]), 10000, (name, stat[1]))


if __name__ == '__main__':
    unittest.main()
