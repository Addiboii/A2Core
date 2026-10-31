import tempfile
import unittest
from pathlib import Path

from tools import skill_audit


def skill(kind='Active', target='Enemy', damage=(1,), **extra):
    return dict({'name': 'S', 'type': kind, 'target': target, 'damage': list(damage)}, **extra)


class SkillAuditTests(unittest.TestCase):
    def audit(self, skills, areas, handled=(), types=None):
        return skill_audit.audit({'skills': skills}, {'skills': areas, 'abnormal_types': types or {}}, {'skills': {}}, {'traps': {}},
                                 set(handled))

    def test_handled_types_are_read_between_the_markers(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'support.cs'
            path.write_text('"Outside"\n// audit:handled-begin\n"Damage", "Teleport_Caster"\n// audit:handled-end\n"After"', encoding='utf-8')
            self.assertEqual(skill_audit.handled_effect_types(path), {'Damage', 'Teleport_Caster'})

    def test_held_abnormal_without_client_type_is_a_cc_hold(self):
        result = self.audit({'13070000': skill()}, {'13070000': {'debuffs': [[100000301, 3000, 3000, 1]]}})
        self.assertIn('cc_hold', result['skills']['13070000']['categories'])

    def test_client_abnormal_types_pick_the_category(self):
        areas = {'13010000': {'debuffs': [[5, 1000, 1000, 0]]}, '13020000': {'debuffs': [[6, 1000, 1000, 0]]}}
        result = self.audit({'13010000': skill(), '13020000': skill()}, areas, types={'5': ['Slow'], '6': ['Stun']})
        self.assertIn('cc_slow', result['skills']['13010000']['categories'])
        self.assertIn('cc_hold', result['skills']['13020000']['categories'])

    def test_teleport_is_missing_until_a_handler_is_listed(self):
        areas = {'13070000': {'effects': [['Teleport_Caster']]}}
        self.assertEqual(skill_audit.status_of('movement_teleport', set())[0], 'missing')
        self.assertEqual(skill_audit.status_of('movement_teleport', {'Teleport_Caster'})[0], 'ok')
        result = self.audit({'13070000': skill()}, areas)
        self.assertIn('movement_teleport', result['skills']['13070000']['categories'])

    def test_effect_types_need_their_handler(self):
        for category, needs in (('heal', 'HpHeal'), ('movement_pull', 'Pull'), ('dispel', 'Dispel_Cc'), ('cooldown', 'DecreaseCooltime'),
                                ('mp_burn', 'MPBurn'), ('aggro', 'AggroTransfer')):
            self.assertEqual(skill_audit.status_of(category, set())[0], 'missing', category)
            self.assertEqual(skill_audit.status_of(category, {needs})[0], 'ok', category)

    def test_effect_rows_land_in_their_categories(self):
        rows = [['HpHealRatio', '2500', '2500'], ['Dispel_Cc'], ['Pull', '200'], ['MPBurn', '1', '2'], ['AggroTransfer', '500'],
                ['FpHeal', '1', '1']]
        result = self.audit({'17100000': skill(damage=())}, {'17100000': {'effects': rows, 'effects_source': 'override'}})
        found = result['skills']['17100000']['categories']
        self.assertEqual(set(found), {'heal', 'dispel', 'movement_pull', 'mp_burn', 'aggro', 'heal_other'})
        self.assertTrue(all('[improvised]' in detail for details in found.values() for detail in details))

    def test_chain_only_skill_without_damage_is_listed_as_missing(self):
        result = self.audit({'15330040': skill(damage=(), chain_prev=15320000, target='MainTarget')}, {'15330040': {}})
        self.assertIn('damage_missing', result['skills']['15330040']['categories'])

    def test_improvised_damage_is_marked_and_counted(self):
        result = self.audit({'15330040': skill(damage_source='override')}, {'15330040': {}})
        self.assertEqual(result['improvised_damage'], 1)
        self.assertIn('[improvised]', result['skills']['15330040']['categories']['damage'][0])

    def test_committed_data_has_no_chain_only_skill_without_damage(self):
        result = skill_audit.audit()
        self.assertEqual([s for s, r in result['skills'].items() if 'damage_missing' in r['categories']], [])
        self.assertEqual([s for s, r in result['skills'].items() if 'unknown' in r['categories']], [])

    def test_abnormal_types_pick_knockback_dot_taunt_and_hot(self):
        areas = {'13010000': {'debuffs': [[1, 1000, 1000, 0], [2, 1000, 1000, 0], [3, 1000, 1000, 0], [4, 1000, 1000, 0]]}}
        types = {'1': ['Knockback', 'Knockdown'], '2': ['Dot_TargetMaxHP'], '3': ['Taunt'], '4': ['Hot_Ratio']}
        result = self.audit({'13010000': skill()}, areas, types=types)
        self.assertEqual(set(result['skills']['13010000']['categories']) - {'damage'},
                         {'movement_knockback', 'cc_hold', 'dot', 'taunt', 'hot'})

    def test_improvised_abnormals_are_flagged(self):
        areas = {'skills': {'13010000': {'debuffs': [[7, 1000, 1000, 0]]}}, 'abnormal_types': {'7': ['Slow']},
                 'abnormal_effects': {'7': {'effects': [[7, 'Slow', '3000']], 'source': 'override'}}}
        result = skill_audit.audit({'skills': {'13010000': skill()}}, areas, {'skills': {}}, {'traps': {}}, set())
        self.assertIn('[improvised]', result['skills']['13010000']['categories']['cc_slow'][0])

    def test_dodge_and_passive_are_not_counted_as_buffs(self):
        result = self.audit({'13000100': skill('Active', 'Self', ()), '13200000': skill('Passive', 'Self', ())}, {})
        self.assertEqual(set(result['skills']['13000100']['categories']), {'dodge'})
        self.assertEqual(set(result['skills']['13200000']['categories']), {'passive'})

    def test_active_skill_without_any_row_is_unknown(self):
        result = self.audit({'13090000': skill(damage=())}, {})
        self.assertIn('unknown', result['skills']['13090000']['categories'])

    def test_abnormal_held_in_some_rows_only_is_reported_wrong(self):
        areas = {'13010000': {'debuffs': [[9, 1, 1, 1]]}, '13020000': {'debuffs': [[9, 1, 1, 0]]}}
        result = self.audit({'13010000': skill(), '13020000': skill()}, areas)
        self.assertEqual(result['wrong'], {9: [{'13020000'}, {'13010000'}]})

    def test_non_player_and_system_skills_are_skipped(self):
        result = self.audit({'90000000': skill(), '13010000': skill('System')}, {})
        self.assertEqual(result['skills'], {})

    def test_spawn_of_a_summon_without_attacks_is_a_marker_unless_a_zone_or_trap_carries_it(self):
        summons = {'skills': {'15280000': {'npc': 1, 'group': ''}, '17150000': {'npc': 1, 'group': ''}, '14170000': {'npc': 1, 'group': ''},
                              '16100000': {'npc': 2, 'group': 'Fire'}},
                   'templates': {'1': {'skills': []}, '2': {'skills': [100011]}}}
        skills = {k: skill(damage=()) for k in ('15280000', '17150000', '14170000', '16100000')}
        result = skill_audit.audit({'skills': skills}, {'skills': {}, 'abnormal_types': {}}, summons,
                                   {'traps': {'14170000': {}}}, set(), {'zones': {'15280000': {}}})
        found = {k: set(v['categories']) for k, v in result['skills'].items()}
        self.assertIn('zone', found['15280000'])
        self.assertNotIn('summon_marker', found['15280000'])
        self.assertIn('summon_marker', found['17150000'])
        self.assertNotIn('summon_marker', found['14170000'])
        self.assertNotIn('summon_marker', found['16100000'])
        self.assertEqual(skill_audit.status_of('summon_marker', set())[0], 'missing')
        self.assertEqual(skill_audit.status_of('zone', set())[0], 'ok')

    def test_bittercold_wind_is_a_zone_in_the_committed_data(self):
        result = skill_audit.audit()
        self.assertIn('zone', result['skills']['15280000']['categories'])
        self.assertNotIn('summon_marker', result['skills']['15280000']['categories'])

    def test_committed_report_is_current(self):
        self.assertEqual(skill_audit.main(['--check']), 0)


if __name__ == '__main__':
    unittest.main()
