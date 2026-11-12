import json
import unittest
from pathlib import Path

DATA = json.loads((Path(__file__).resolve().parents[1] / 'tools/data/sealstones.json').read_text(encoding='utf-8'))


class SealStoneTests(unittest.TestCase):
    def test_recorded_stones(self):
        # 2026-11-06 234912 i24888 / i22660: 534 SealStoneAmplifyDamage 500 for 533460001 and 1000 for 533460004.
        self.assertEqual(DATA['stones']['533460001'], 'SealStone_Lowest_1')
        self.assertEqual(DATA['stones']['533460004'], 'SealStone_High_1')
        self.assertIn([534, 'SealStoneAmplifyDamage', 500], DATA['effects']['SealStone_Lowest_1'])
        self.assertIn([534, 'SealStoneAmplifyDamage', 1000], DATA['effects']['SealStone_High_1'])
        self.assertIn([69, 'SealStoneAddDamage', 20], DATA['effects']['SealStone_High_1'])

    def test_every_stone_names_an_effect(self):
        self.assertTrue(DATA['stones'])
        for name in DATA['stones'].values():
            self.assertIn(name, DATA['effects'])


if __name__ == '__main__':
    unittest.main()
