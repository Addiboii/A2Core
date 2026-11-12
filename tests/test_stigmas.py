import json
import tempfile
import unittest
from pathlib import Path

from tools import stigmas

DATA = json.loads(stigmas.DATA.read_text(encoding='utf-8'))
CLASSES = ['Assassin', 'Chanter', 'Cleric', 'Elementalist', 'Fighter', 'Gladiator', 'Ranger', 'Sorcerer', 'Templar']
PREFIX = {'Gladiator': 11, 'Templar': 12, 'Assassin': 13, 'Ranger': 14, 'Sorcerer': 15, 'Elementalist': 16, 'Cleric': 17, 'Chanter': 18,
          'Fighter': 19}


class StigmaDataTests(unittest.TestCase):
    """tools/data/stigmas.json as built from client build 26262663."""

    def test_thirteen_stigmas_per_class_for_all_nine_classes(self):
        self.assertEqual(sorted(DATA['classes']), CLASSES)
        self.assertEqual(DATA['build'], '26262663')
        every = [s for cls in DATA['classes'].values() for s in cls['skills']]
        self.assertEqual(len(every), 117)
        self.assertEqual(len(set(every)), 117)
        for name, cls in DATA['classes'].items():
            with self.subTest(name):
                self.assertEqual(len(cls['skills']), 13)
                self.assertTrue(all(s // 1_000_000 == PREFIX[name] for s in cls['skills']))
                self.assertEqual((cls['need_level'], cls['max_level']), (22, 20))

    def test_cost_ladder_is_1_2_4_8_per_five_levels(self):
        for name, cls in DATA['classes'].items():
            with self.subTest(name):
                self.assertEqual([row[1] for row in cls['ladder']], [1] * 5 + [2] * 5 + [4] * 5 + [8] * 5)
                self.assertEqual([row[0] for row in cls['ladder']], list(range(1, 21)))
                self.assertTrue(all(row[2] == 22 for row in cls['ladder']))
                # Learning (level 1) needs AscensionGrade_3; the other levels name no grade.
                self.assertEqual([row[3] for row in cls['ladder']], [3] + [0] * 19)

    def test_every_stigma_has_its_four_variants(self):
        self.assertEqual(DATA['variants']['14310000'], [[5, 14310010], [10, 14310020], [15, 14310030], [20, 14310040]])
        bases = [s for cls in DATA['classes'].values() for s in cls['skills']]
        self.assertTrue(all(len(DATA['variants'][str(b)]) == 4 for b in bases))
        self.assertTrue(all(str(v) in DATA['skills'] for steps in DATA['variants'].values() for _, v in steps))

    def test_shards_slots_and_levels_from_exp_dat(self):
        self.assertEqual([DATA['shards'][str(level)] for level in (22, 23, 40, 45)], [0, 1, 19, 29])
        self.assertEqual(DATA['slot_levels'], [22, 27, 32, 37])
        self.assertEqual(DATA['slots'], [20, 21, 22, 23])   # wire slots: QuickSlotData SlotId - 1 (OBSERVED 2026-11-05)
        self.assertEqual(max(DATA['slot_max'].values()), 4)
        totals = [DATA['shards'][str(level)] for level in range(1, 101)]
        self.assertEqual(totals, sorted(totals))

    def test_every_stigma_has_cast_data_of_this_build(self):
        for name, cls in DATA['classes'].items():
            for skill in cls['skills']:
                row = DATA['skills'][str(skill)]
                self.assertEqual(row['class'], name)
                self.assertEqual(row['type'], 'Active')
        # Skill.dat NeedCoolTime on 26262663 (90000 on 2.0.6).
        self.assertEqual(DATA['skills']['13310000']['cooldown_ms'], 60000)
        self.assertGreater(sum(1 for row in DATA['skills'].values() if row['damage']), 50)
        self.assertEqual(DATA['missing_skills'], [])
        self.assertTrue(DATA['unknown'])


def _table(rows):
    return {'Properties': {'Data': rows}}


class StigmaBuilderTests(unittest.TestCase):
    def build(self, acquire, exp, quick):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'tables').mkdir()
            (root / 'exp').mkdir()
            (root / 'tables/table-002.json').write_text(json.dumps(_table(acquire)), encoding='utf-8')
            (root / 'tables/table-006.json').write_text(json.dumps(_table(quick)), encoding='utf-8')
            (root / 'exp/table-000.json').write_text(json.dumps(_table(exp)), encoding='utf-8')
            skill = {'kind': 'skills', 'id': 11240000, 'name': 'Test', 'facts': {'SkillType': 'ESkillType::Active', 'NeedCoolTime': 30000.0},
                     'raw': {}, 'relationships': {'effects': [{'rows': [{'ID': {'Value': 7}, 'EffectType': 'ESkillEffectType::Damage',
                                                                          'EffectValueList': ['10', '20', '3000', '4000', '0']}]}]}}
            variant = dict(skill, id=11240010)
            (root / 'catalog.jsonl').write_text(json.dumps(skill) + '\n' + json.dumps(variant) + '\n', encoding='utf-8')
            return stigmas.build(root / 'tables', root / 'exp', root / 'catalog.jsonl')

    @staticmethod
    def row(level, cost, grade='EAscensionGrade::None', ui=True):
        return {'ClassType': 'ECharacterClass::Gladiator', 'AcquireType': 'ESkillAcquireType::Stigma', 'SkillId': {'Value': 11240000},
                'SkillLevel': level, 'NeedCharacterLevel': 22, 'NeedAscensionGrade': grade, 'CostStigmaPoint': cost,
                'CostStigmaSuperiorPoint': 0, 'bSkillListUI': ui}

    def test_build_reads_rows_exp_and_slots(self):
        mastery = dict(self.row(2, 0), AcquireType='ESkillAcquireType::Mastery', SkillId={'Value': 11010000})
        acquire = [self.row(1, 1, 'EAscensionGrade::AscensionGrade_3'), self.row(2, 2), mastery]
        exp = [{'Level': 21, 'BonusStigmaPoint': 0, 'StigmaSkillContextSlotMax': 0},
               {'Level': 22, 'BonusStigmaPoint': 0, 'StigmaSkillContextSlotMax': 1},
               {'Level': 23, 'BonusStigmaPoint': 1, 'StigmaSkillContextSlotMax': 2}]
        quick = [{'SlotId': {'Value': 1}, 'InputAction': 'EAionInputAction::QuickSlot_Skill_1'},
                 {'SlotId': {'Value': 22}, 'InputAction': 'EAionInputAction::QuickSlot_Stigma_2'},
                 {'SlotId': {'Value': 21}, 'InputAction': 'EAionInputAction::QuickSlot_Stigma_1'}]
        data = self.build(acquire, exp, quick)
        self.assertEqual(data['classes']['Gladiator']['ladder'], [[1, 1, 22, 3], [2, 2, 22, 0]])
        self.assertEqual(data['classes']['Gladiator']['skills'], [11240000])
        self.assertEqual(data['slots'], [20, 21])
        self.assertEqual(data['slot_levels'], [22, 23])
        self.assertEqual(data['shards'], {'21': 0, '22': 0, '23': 1})
        self.assertEqual(data['skills']['11240000']['cooldown_ms'], 30000)
        self.assertEqual(data['variants'], {'11240000': [[5, 11240010]]})   # only the variants Skill.dat has
        self.assertEqual(data['skills']['11240010']['class'], 'Gladiator')
        self.assertEqual(data['skills']['11240000']['damage'][0]['ratio'], [3000, 4000])

    def test_build_refuses_disagreeing_ladders_and_missing_slots(self):
        other = dict(self.row(1, 2), SkillId={'Value': 11250000})
        with self.assertRaises(ValueError):
            self.build([self.row(1, 1), other], [], [])
        exp = [{'Level': 22, 'BonusStigmaPoint': 0, 'StigmaSkillContextSlotMax': 1}]
        with self.assertRaises(ValueError):
            self.build([self.row(1, 1)], exp, [])

    def test_a_missing_skill_is_listed_not_invented(self):
        data = self.build([dict(self.row(1, 1), SkillId={'Value': 11990000})], [], [])
        self.assertEqual(data['missing_skills'], [11990000])
        self.assertNotIn('11990000', data['skills'])


if __name__ == '__main__':
    unittest.main()
