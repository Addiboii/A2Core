import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/elemental-summons.json'
PC = ROOT / 'tools/data/pc-summons.json'


class ElementalSummonsDataTests(unittest.TestCase):
    def test_enum_matches_usmap_order(self):
        doc = json.loads(DATA.read_text(encoding='utf-8'))
        self.assertEqual(
            {'None': 0, 'Fire': 1, 'Water': 2, 'Wind': 3, 'Earth': 4, 'Ancient': 5, 'Max': 6},
            {**doc['enum'], 'None': 0, 'Max': 6},
        )

    def test_every_elemental_pc_summon_npc_is_mapped(self):
        doc = json.loads(DATA.read_text(encoding='utf-8'))
        summons = json.loads(PC.read_text(encoding='utf-8'))['skills']
        by_npc = doc['by_npc']
        enum = doc['enum']
        for skill_id, row in summons.items():
            if not (row.get('group') or '').strip():
                continue
            npc = str(row['npc'])
            self.assertIn(npc, by_npc, msg=f'skill {skill_id} npc {npc}')
            self.assertEqual(by_npc[npc], enum[row['group']])

    def test_wind_and_earth_are_not_swapped(self):
        by_npc = json.loads(DATA.read_text(encoding='utf-8'))['by_npc']
        self.assertEqual(3, by_npc['2920141'])
        self.assertEqual(4, by_npc['2920161'])


if __name__ == '__main__':
    unittest.main()
