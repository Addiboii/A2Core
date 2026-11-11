import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import wing_data  # noqa: E402

DATA = json.loads((ROOT / 'tools/data/wings.json').read_text(encoding='utf-8'))


class WingBuilderTests(unittest.TestCase):
    def test_wing_rows(self):
        rows = [{'ID': {'Value': 40200601}, 'Name': 'Wing_D_X', 'Grade': 'EItemGrade::Common', 'bShow': True, 'Order': 2,
                 'PcRace': 'ERace::Dark', 'EnchantGroup': 'None', 'EffectGroup': 'G',
                 'EquipStatList': [{'Stat': 'EStat::HPMax', 'Value': 200}]},
                {'ID': {'Value': 30200600}, 'Name': 'Wing_L_X', 'Grade': 'EItemGrade::Rare', 'bShow': False, 'Order': 1,
                 'PcRace': 'ERace::Light', 'EnchantGroup': 'None', 'EffectGroup': 'G', 'EquipStatList': []}]
        wings = wing_data.wings_from(rows)
        self.assertEqual(list(wings), ['30200600', '40200601'])
        self.assertEqual(wings['40200601'], {'name': 'Wing_D_X', 'race': 2, 'grade': 'Common', 'show': True, 'order': 2,
                                             'enchant_group': 'None', 'effect_group': 'G', 'stats': [['HPMax', 200]]})
        self.assertEqual(wings['30200600']['race'], 1)

    def test_effect_levels_index_by_level(self):
        rows = [{'EffectGroup': 'G', 'Level': 2, 'Stats': [{'Key': 'EStat::HPMax', 'Value': 60}]},
                {'EffectGroup': 'G', 'Level': 0, 'Stats': [{'Key': 'EStat::FPMax', 'Value': 250000}]}]
        self.assertEqual(wing_data.effects_from(rows), {'G': [[['FPMax', 250000]], [], [['HPMax', 60]]]})


class WingDataTests(unittest.TestCase):
    def test_build_26262663_has_84_wings_42_per_race(self):
        # OBSERVED: Wing.dat of build 26262663 (research/local/ascension-flight-tables/table-000.json).
        self.assertEqual(DATA['build'], '26262663')
        races = [w['race'] for w in DATA['wings'].values()]
        self.assertEqual((len(races), races.count(1), races.count(2)), (84, 42, 42))
        for wing_id, wing in DATA['wings'].items():
            self.assertEqual(wing['race'], 1 if wing_id.startswith('3') else 2, wing_id)

    def test_default_wings_and_stats(self):
        self.assertEqual(DATA['wings']['30200600']['stats'], [['HPMax', 200]])
        self.assertEqual(DATA['wings']['40200601']['stats'], [['HPMax', 200]])
        self.assertEqual(DATA['wings']['30100300']['stats'],
                         [['AbnormalResistance', 300], ['AbnormalAccuracy', 300], ['HPMax', 500], ['Defense', 400]])

    def test_no_wing_enchants_and_no_effects_from_another_build(self):
        self.assertTrue(all(w['enchant_group'] == 'None' for w in DATA['wings'].values()))
        # WingEnchantEffect.dat was not extracted from build 26262663: nothing is carried over from another build.
        self.assertIsNone(DATA['effects_source'])
        self.assertEqual(DATA['effects'], {})


if __name__ == '__main__':
    unittest.main()
