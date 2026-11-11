import json
import unittest

from tools import skill_effect_conditions


def effect(eid, group, *conds):
    return {'ID': {'Value': eid}, 'SkillEffectGroupId': {'Value': group}, 'EffectType': 'ESkillEffectType::Damage',
            'SkillEffectConditionList': [{'ConditionType': f'ESkillEffectConditionType::{t}', 'ConditionValues': v,
                                          'ConditionalExpression': e} for t, v, e in conds]}


class SkillEffectConditionTests(unittest.TestCase):
    def test_false_expression_negates(self):
        effects, groups = skill_effect_conditions.conditions([
            effect(11, 1, ('IsMainTarget', ['0', '0'], True)),
            effect(12, 1, ('IsMainTarget', ['0', '0'], False), ('Caster_HasAbnormalId', [1004901, '0'], False)),
            effect(13, 1)])
        self.assertEqual(effects, {11: [1, [['IsMainTarget', ['0', '0'], False]]],
                                   12: [1, [['IsMainTarget', ['0', '0'], True], ['Caster_HasAbnormalId', ['1004901', '0'], True]]]})
        rows = [{'ID': {'Value': 7}, 'SkillEffectTimeDataList': [{'SkillEffectGroupId': {'Value': 1}}, {'SkillEffectGroupId': {'Value': 1}}]}]
        self.assertEqual(skill_effect_conditions.skills(rows, groups), {7: [11, 12]})

    def test_tracked_file_is_from_build_26262663(self):
        text = skill_effect_conditions.OUT.read_text(encoding='utf-8')
        doc = json.loads(text)
        self.assertNotIn('\n ', text)
        self.assertEqual(doc['build'], '26262663')
        self.assertEqual(len(doc['effects']), 10003)   # SkillEffect rows with a SkillEffectConditionList
        self.assertEqual(doc['skills']['15200058'], [1520005811])
        self.assertEqual(doc['effects']['1520005811'], [152000581, [['Target_HasAbnormalGroupId', ['152000021', '0'], False]]])
        self.assertEqual(doc['effects']['1604000012'][1], [['IsMainTarget', ['0', '0'], True]])


if __name__ == '__main__':
    unittest.main()
