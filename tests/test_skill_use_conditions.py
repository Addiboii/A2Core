import json
import os
import tempfile
import unittest
from pathlib import Path

from tools import skill_use_conditions


class SkillUseConditionTests(unittest.TestCase):
    def test_basic_auto_attack_skips_all_use_condition_fields(self):
        skill = {
            'type': 'Active',
            'target': 'MainTarget',
            'context_register': 'FixSlot',
            'cooldown_ms': 0,
            'chain_prev': 0,
            'damage': [{}],
            'cost': {'mp': 0, 'hp': 0, 'sp': 0},
        }
        entry = {
            'facts': {
                'CanUseWhenPeaceState': True,
                'CanUseWhenElementalSummonSpawned': ['EElementalSummonGroupType::Fire'],
                'CanUseSkillCostType': 'ECanUseSkillCostType::MPRatio_Less',
                'CanUseSkillCostValue': 5000,
            },
        }
        self.assertEqual(skill_use_conditions.use_condition_fields(entry, 15_210_000, skill), {})

    def test_summon_cast_skills_ignore_elemental_summon_spawned_field(self):
        entry = {
            'facts': {
                'CanUseWhenElementalSummonSpawned': [
                    'EElementalSummonGroupType::Fire',
                    'EElementalSummonGroupType::Water',
                ],
            },
        }
        self.assertEqual(skill_use_conditions.use_condition_fields(entry, 16_140_000), {'needs_spirit': ['Fire', 'Water']})
        self.assertEqual(skill_use_conditions.use_condition_fields(entry, 16_100_000), {})

    def test_real_field_is_read_from_facts_and_ignores_none(self):
        entry = {'facts': {'CanUseWhenElementalSummonSpawned': ['EElementalSummonGroupType::Fire', 'EElementalSummonGroupType::None']}}
        self.assertEqual(skill_use_conditions.use_condition_fields(entry), {'needs_spirit': ['Fire']})
        self.assertEqual(skill_use_conditions.use_condition_fields({'facts': {}, 'raw': {}}), {})

    def test_patch_strips_use_conditions_from_basic_auto_attacks(self):
        doc = {'skills': {'15210000': {'type': 'Active', 'target': 'MainTarget', 'context_register': 'FixSlot',
                                      'cooldown_ms': 0, 'chain_prev': 0, 'damage': [{}], 'cost': {'mp': 0, 'hp': 0, 'sp': 0},
                                      'needs_peace': True},
                       '16140000': {'type': 'Active'}}}
        patched, _ = skill_use_conditions.patch(doc)
        self.assertNotIn('15210000', patched)
        self.assertNotIn('needs_peace', doc['skills']['15210000'])
        self.assertEqual(patched, ['16140000'])

    def test_jointstrike_gets_an_improvised_condition_for_every_variant(self):
        doc = {'skills': {'16140000': {'type': 'Active'}, '16140010': {'type': 'Active'}, '16040000': {'type': 'Active'}}}
        patched, _ = skill_use_conditions.patch(doc)
        self.assertEqual(patched, ['16140000', '16140010'])
        self.assertEqual(doc['skills']['16140010']['use_condition_source'], 'override')
        self.assertNotIn('needs_spirit', doc['skills']['16040000'])

    def test_peace_and_resource_fields_export(self):
        entry = {
            'facts': {
                'CanUseWhenPeaceState': True,
                'CanUseSkillCostType': 'ECanUseSkillCostType::HPRatio_Less',
                'CanUseSkillCostValue': 3000,
            },
        }
        fields = skill_use_conditions.use_condition_fields(entry)
        self.assertTrue(fields['needs_peace'])
        self.assertEqual(fields['needs_resource'], {'type': 'HPRatio_Less', 'value': 3000})

    def test_skill_dat_export_wins_over_override_for_spirit(self):
        with tempfile.TemporaryDirectory() as tmp:
            table_dir = Path(tmp) / 'skill-tables-test'
            table_dir.mkdir()
            row = {
                'ID': {'Value': 16_140_000},
                'CanUseWhenElementalSummonSpawned': ['EElementalSummonGroupType::Fire'],
            }
            table_dir.joinpath('table-000.json').write_text(
                json.dumps({'Properties': {'Data': [row]}}), encoding='utf-8')
            old = os.environ.get('AION_RESEARCH_LOCAL')
            os.environ['AION_RESEARCH_LOCAL'] = tmp
            try:
                skill_use_conditions.LOCAL = Path(tmp)
                by_skill, src = skill_use_conditions.export_from_skill_dat()
                self.assertNotEqual(src, 'missing')
                self.assertEqual(by_skill[16_140_000]['needs_spirit'], ['Fire'])
                doc = {'skills': {'16140000': {'type': 'Active'}}}
                patched, counts = skill_use_conditions.patch(doc)
                self.assertEqual(patched, ['16140000'])
                self.assertEqual(doc['skills']['16140000']['needs_spirit'], ['Fire'])
                self.assertEqual(doc['skills']['16140000']['use_condition_source'], 'Skill.dat')
                self.assertEqual(counts['needs_spirit'], 1)
            finally:
                if old is None:
                    os.environ.pop('AION_RESEARCH_LOCAL', None)
                else:
                    os.environ['AION_RESEARCH_LOCAL'] = old
                skill_use_conditions.LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', skill_use_conditions.ROOT / 'research/local'))

    def test_skill_dat_export_peace_and_resource(self):
        with tempfile.TemporaryDirectory() as tmp:
            table_dir = Path(tmp) / 'skill-tables-20261030'
            table_dir.mkdir()
            row = {
                'ID': {'Value': 12_010_000},
                'CanUseWhenPeaceState': True,
                'CanUseSkillCostType': 'ECanUseSkillCostType::HPRatio_Less',
                'CanUseSkillCostValue': 2500,
            }
            table_dir.joinpath('table-000.json').write_text(
                json.dumps({'Properties': {'Data': [row]}}), encoding='utf-8')
            os.environ['AION_RESEARCH_LOCAL'] = tmp
            skill_use_conditions.LOCAL = Path(tmp)
            doc = {'skills': {'12010000': {'type': 'Active', 'name': 'Test', 'target': 'MainSelf', 'context_register': 'FixSlot',
                                           'cooldown_ms': 0, 'chain_prev': 0, 'damage': [{}], 'cost': {'mp': 0, 'hp': 0, 'sp': 0}}}}
            patched, counts = skill_use_conditions.patch(doc)
            self.assertEqual(patched, [])
            skill = doc['skills']['12010000']
            self.assertNotIn('needs_peace', skill)
            self.assertNotIn('needs_resource', skill)


if __name__ == '__main__':
    unittest.main()
