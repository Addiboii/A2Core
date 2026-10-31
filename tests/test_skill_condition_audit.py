import unittest
from pathlib import Path

from tools import skill_condition_audit, skill_condition_kinds


class SkillConditionAuditTests(unittest.TestCase):
    def test_usmap_enums_include_skill_cond_types(self):
        enums = skill_condition_kinds.load_enums()
        names = skill_condition_kinds.enum_names(enums, 'ESkillCondType')
        self.assertIn('HasAbnormalGroupId', names)
        self.assertIn('HasAbnormalEffectType', names)

    def test_audit_generates_markdown(self):
        text = skill_condition_audit.render(
            skill_condition_kinds.load_enums(),
            skill_condition_audit.collect_rows(skill_condition_audit.load_json('class-skills.json')),
            *skill_condition_audit.build_producers(
                skill_condition_audit.load_json('class-skills.json'),
                skill_condition_audit.load_json('skill-areas.json')
                if (Path(skill_condition_audit.DATA) / 'skill-areas.json').is_file()
                else {},
            ),
        )
        self.assertIn('# Skill condition audit', text)
        self.assertIn('HasAbnormalEffectType', text)
        self.assertIn('Stagger', text)


if __name__ == '__main__':
    unittest.main()
