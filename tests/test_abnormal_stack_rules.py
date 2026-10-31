import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys_path = ROOT / 'tools'
import sys
sys.path.insert(0, str(sys_path))
from abnormal_stack_rules import build  # noqa: E402


class AbnormalStackRulesTests(unittest.TestCase):
    def test_build_emits_group_and_overlap_defaults(self):
        doc = build()
        self.assertIn('abnormals', doc)
        self.assertTrue(len(doc['abnormals']) > 0)
        sample = next(iter(doc['abnormals'].values()))
        self.assertIn('group', sample)
        self.assertEqual('Renew', sample['overlap_time'])
        self.assertEqual('Replace', sample['replace'])


if __name__ == '__main__':
    unittest.main()
