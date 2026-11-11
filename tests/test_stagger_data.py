import json
import unittest

from tools import stagger_data


def effect(eid, group, kind, value):
    return {'ID': {'Value': eid}, 'SkillEffectGroupId': {'Value': group}, 'EffectType': f'ESkillEffectType::{kind}',
            'EffectValueList': [str(value)]}


def skill(sid, *groups):
    return {'ID': {'Value': sid}, 'SkillEffectTimeDataList': [{'SkillEffectGroupId': {'Value': g}} for g in groups]}


class StaggerBuilderTests(unittest.TestCase):
    def test_rows_skills_and_npcs(self):
        decrease, increase, groups = stagger_data.effects([
            effect(11, 1, 'Damage', 70), effect(12, 1, 'GroggyGuardDecreaseEnergy', 10),
            effect(21, 2, 'GroggyGuardIncreaseEnergy', 5), effect(31, 3, 'Damage', 1)])
        self.assertEqual((decrease, increase), ({12: 10}, {21: 5}))
        # seq is the row's place in SkillEffectTimeDataList (the hit's seq on the wire: 11190000's drain rides hit 3)
        self.assertEqual(stagger_data.skills([skill(7, 3, 0, 1), skill(8, 3), skill(9, 2)], groups), {7: [[3, 12]], 9: [[1, 21]]})
        npc = [{'ID': {'Value': 5}, 'NpcGroggyType': 'ENpcGroggyType::GroggyGuard', 'GroggyTime': 5000},
               {'ID': {'Value': 6}, 'NpcGroggyType': 'ENpcGroggyType::None', 'GroggyTime': 0}]
        self.assertEqual(stagger_data.npcs(npc), {5: ['GroggyGuard', 5000]})

    def test_tracked_file_is_from_build_26262663(self):
        text = stagger_data.OUT.read_text(encoding='utf-8')
        doc = json.loads(text)
        self.assertNotIn('\n ', text)
        self.assertEqual(doc['build'], '26262663')
        self.assertEqual(len(doc['decrease']), 2392)   # every GroggyGuardDecreaseEnergy row of SkillEffect.dat
        self.assertEqual(len(doc['increase']), 6)

        def drain(sid):
            return sum(doc['decrease'].get(str(e), 0) for _, e in doc['skills'][str(sid)])

        # Table values (SkillEffect.dat, 26262663) of the five Elementalist skills, and the four skills whose drains the
        # 2.0.6 recording 20261019-231752 shows on the boss 2701034's guard (50 -> 40 -> 25 -> 23 ...).
        for sid, value in {16040000: 3, 16140000: 10, 16330000: 5, 16220000: 20, 16240000: 50,
                           11190000: 10, 11290001: 15, 11010000: 2, 11420000: 2}.items():
            self.assertEqual(drain(sid), value, sid)
        self.assertEqual(doc['skills']['11190000'], [[3, 1119000022]])
        self.assertEqual(doc['npcs']['2701034'], ['GroggyGuard', 5000])
        self.assertNotIn('11020000', doc['skills'])   # the basic attack drains nothing (none in the recording either)


if __name__ == '__main__':
    unittest.main()
