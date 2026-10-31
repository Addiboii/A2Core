import unittest

from tools import skill_cond


class SkillCondExportTests(unittest.TestCase):
    def test_ranger_override_merges_into_doc(self):
        doc = {'skills': {
            '14070000': {'type': 'Active', 'name': 'Suppressing Arrow'},
            '14090000': {'type': 'Active', 'name': 'Marking Shot'},
            '14010000': {'type': 'Active'},
        }}
        needs, grants = skill_cond.patch(doc)
        self.assertIn('14070000', needs)
        self.assertIn('14090000', grants)
        self.assertEqual(doc['skills']['14070000']['skill_cond']['abnormal_group'], 140900001)
        self.assertEqual(doc['skills']['14070000']['skill_cond']['select_target'], 'Self')
        self.assertEqual(doc['skills']['14090000']['skill_cond_grant']['duration_ms'], 10000)
        self.assertEqual(doc['skills']['14090000']['skill_cond_grant']['select_target'], 'Self')
        self.assertNotIn('skill_cond', doc['skills']['14010000'])


if __name__ == '__main__':
    unittest.main()
