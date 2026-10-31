import json
import tempfile
import unittest
from pathlib import Path

from tools import skill_areas


def table(rows):
    return {'Properties': {'Data': rows}}


def timed(projectile, filter_id=0):
    return {'SkillEffectFilterId': {'Value': filter_id}, 'ProjectileId': {'Value': projectile}}


class SkillAreasBuildTests(unittest.TestCase):
    def build(self, skills, projectiles, class_skills, npc_skills=(), world_skills=(), filters=()):
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp)
            files = {'s': table(skills), 'f': table(list(filters)), 'p': table([{'ID': {'Value': p}, 'Speed': 5000} for p in projectiles]),
                     'c': {'skills': {str(k): {} for k in class_skills}}, 'n': {'npc_skills': {str(k): {} for k in npc_skills}},
                     'w': {'npc_skills': {str(k): {} for k in world_skills}}}
            for name, value in files.items():
                (d / name).write_text(json.dumps(value), encoding='utf-8')
            return skill_areas.build(d / 's', d / 'f', d / 'p', d / 'c', d / 'n', d / 'w')['skills']

    def test_every_timed_row_names_its_projectile_and_chain_steps_are_included(self):
        out = self.build([{'ID': {'Value': 14030000}, 'SkillEffectTimeDataList': [timed(140300001), timed(140300002), timed(0)]}],
                         [140300001, 140300002], [14030000])
        self.assertEqual(out['14030000']['projectile'], 140300001)
        self.assertEqual(out['14030000']['projectiles'], [140300001, 140300002, 0])

    def test_summon_npc_skills_are_exported(self):
        out = self.build([{'ID': {'Value': 16001105}, 'SkillEffectTimeDataList': [timed(160011051)]}], [160011051], [], [16001105])
        self.assertEqual(out['16001105']['projectiles'], [160011051])

    def test_monster_and_boss_npc_skills_get_their_area(self):
        area = {'ID': {'Value': 90000001}, 'bRelationshipEnemy': True, 'EffectRangeType': 'EEffectRangeType::Circle',
                'EffectRangeValues': [0, 0, 0, 0, 600, 600], 'TargetCountMax': 8, 'bEffectRangeNeedTarget': False}
        out = self.build([{'ID': {'Value': 9000000}, 'SkillEffectTimeDataList': [timed(0, 90000001)]}], [], [],
                         world_skills=[9000000], filters=[area])
        self.assertEqual(out['9000000'], {'shape': 'Circle', 'values': [0, 0, 0, 0, 600, 600], 'max': 8, 'on_target': False})

    def test_ground_skill_exports_ground_type_radius_and_stun_debuff(self):
        area = {'ID': {'Value': 141700001}, 'bRelationshipEnemy': True, 'EffectRangeType': 'EEffectRangeType::Circle',
                'EffectRangeValues': [0, 0, 0, 0, 500, 500], 'TargetCountMax': 8, 'bEffectRangeNeedTarget': False}
        skill = {'ID': {'Value': 14170000}, 'GroundType': 'ESkillGroundType::Location', 'GroundRadius': 500,
                 'SkillEffectTimeDataList': [dict(timed(0, 141700001), SkillEffectGroupId={'Value': 1417000011})]}
        effects = [{'SkillEffectGroupId': {'Value': 1417000011}, 'EffectType': 'ESkillEffectType::Abnormal_Cc',
                    'EffectValueList': ['2000', '2000', '141700011']},
                   {'SkillEffectGroupId': {'Value': 1417000011}, 'EffectType': 'ESkillEffectType::Abnormal_True',
                    'EffectValueList': ['3000', '3000', '141700012']}]
        abnormal_effects = [{'AbnormalId': {'Value': 141700011}, 'AbnormalEffectType': 'EAbnormalEffectType::Stun'},
                            {'AbnormalId': {'Value': 141700012}, 'AbnormalEffectType': 'EAbnormalEffectType::MoveSpeed'}]
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp)
            for name, value in {'s': table([skill]), 'f': table([area]), 'p': table([]), 'c': {'skills': {'14170000': {}}},
                                'e': table(effects), 'a': table(abnormal_effects)}.items():
                (d / name).write_text(json.dumps(value), encoding='utf-8')
            out = skill_areas.build(d / 's', d / 'f', d / 'p', d / 'c', d / 'none', d / 'none', d / 'e', d / 'a')['skills']
        entry = out['14170000']
        self.assertEqual((entry['ground'], entry['ground_radius'], entry['max'], entry['on_target']), ('Location', 500, 8, False))
        self.assertEqual(entry['debuffs'], [[141700011, 2000, 2000, 1], [141700012, 3000, 3000, 0]])

    def test_default_ground_type_alone_is_not_ground_and_unknown_cc_is_not_held(self):
        skills = [{'ID': {'Value': 1}, 'GroundType': 'ESkillGroundType::Location', 'TargetProcessType': 'ESkillTargetProcessType::MainTarget',
                   'SkillEffectTimeDataList': [dict(timed(0), SkillEffectGroupId={'Value': 10})]},
                  {'ID': {'Value': 2}, 'GroundType': 'ESkillGroundType::Location', 'TargetProcessType': 'ESkillTargetProcessType::Ground',
                   'SkillEffectTimeDataList': [dict(timed(0), SkillEffectGroupId={'Value': 20})]}]
        cc = lambda group, ms, abnormal, condition=None: {'SkillEffectGroupId': {'Value': group}, 'EffectType': 'ESkillEffectType::Abnormal_Cc',
                                                          'EffectValueList': [ms, ms, abnormal], 'SkillEffectConditionList': condition or []}
        effects = [cc(10, '2000', '555'), cc(20, '4000', '100000311', [{'x': 1}]), cc(20, '3000', '100000311'), cc(20, '10000', '100000311', [{'x': 2}])]
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp)
            for name, value in {'s': table(skills), 'f': table([]), 'p': table([]), 'c': {'skills': {'1': {}, '2': {}}}, 'e': table(effects)}.items():
                (d / name).write_text(json.dumps(value), encoding='utf-8')
            out = skill_areas.build(d / 's', d / 'f', d / 'p', d / 'c', d / 'none', d / 'none', d / 'e', d / 'missing')['skills']
        self.assertNotIn('ground', out['1'])
        self.assertEqual(out['1']['debuffs'], [[555, 2000, 2000, 0]])
        self.assertEqual(out['2']['ground'], 'Location')
        self.assertEqual(out['2']['debuffs'], [[100000311, 3000, 3000, 1]])


class SkillEffectExportTests(unittest.TestCase):
    def test_effect_rows_other_than_damage_and_debuffs_are_exported_per_skill(self):
        skills = [{'ID': {'Value': 13070000}, 'SkillEffectTimeDataList': [dict(timed(0), SkillEffectGroupId={'Value': 7})]}]
        effects = [{'SkillEffectGroupId': {'Value': 7}, 'EffectType': 'ESkillEffectType::Teleport_Caster', 'EffectValueList': ['120']},
                   {'SkillEffectGroupId': {'Value': 7}, 'EffectType': 'ESkillEffectType::Damage', 'EffectValueList': ['1']},
                   {'SkillEffectGroupId': {'Value': 7}, 'EffectType': 'ESkillEffectType::Abnormal_Cc', 'EffectValueList': ['3000', '3000', '9']}]
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp)
            for name, value in {'s': table(skills), 'f': table([]), 'p': table([]), 'c': {'skills': {'13070000': {}}}, 'e': table(effects)}.items():
                (d / name).write_text(json.dumps(value), encoding='utf-8')
            out = skill_areas.build(d / 's', d / 'f', d / 'p', d / 'c', d / 'none', d / 'none', d / 'e', d / 'missing')['skills']
        self.assertEqual(out['13070000']['effects'], [['Teleport_Caster', '120']])

    def test_overrides_cover_the_skill_family_and_never_replace_an_export(self):
        skills = {'13070001': {'effects': [['Pull']]}}
        class_skills = {'13070000': {}, '13070001': {}, '13070002': {}, '13080000': {}}
        overrides = {'skills': {'13070000': {'family': True, 'effects': [['Teleport_Caster']]}}}
        applied = skill_areas.apply_overrides(skills, overrides, class_skills)
        self.assertEqual(sorted(applied), ['13070000', '13070002'])
        self.assertEqual(skills['13070000'], {'effects': [['Teleport_Caster']], 'effects_source': 'override'})
        self.assertEqual(skills['13070001'], {'effects': [['Pull']]})
        self.assertNotIn('13080000', skills)

    def test_split_debuff_targets_keeps_taunt_on_enemy_and_grant_off_attack_export(self):
        skills = {'11290000': {'debuffs': [[112900001, 50, 50, 0], [112900072, 1700, 1700, 0], [100000321, 3000, 3000, 1]]}}
        class_doc = {'skills': {'11290000': {'skill_cond_grant': {'abnormal': 112900001}, 'target': 'MainTarget', 'damage': [{}]}}}
        abnormal_types = {'112900072': ['Taunt'], '100000321': ['Knockback']}
        changed = skill_areas.assign_debuff_targets(skills, class_doc, abnormal_types)
        self.assertEqual(1, changed)
        self.assertNotIn('caster_debuffs', skills['11290000'])
        self.assertEqual(2, len(skills['11290000']['target_debuffs']))

    def test_shadowstrike_stun_is_held_and_teleports(self):
        data = json.loads(skill_areas.DATA.read_text(encoding='utf-8'))['skills']
        self.assertEqual(data['13070000']['debuffs'], [[100000301, 3000, 3000, 1]])
        self.assertEqual(data['13070000']['effects'], [['Teleport_Caster']])

    def test_mark_known_held_flags_only_known_abnormals(self):
        skills = {'1': {'debuffs': [[100000301, 1, 1, 0], [555, 1, 1, 0]]}}
        self.assertEqual(skill_areas.mark_known_held(skills), 1)
        self.assertEqual(skills['1']['debuffs'], [[100000301, 1, 1, 1], [555, 1, 1, 0]])


if __name__ == '__main__':
    unittest.main()
