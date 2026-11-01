import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class SkillApCostsTests(unittest.TestCase):
    def test_overrides_file_exists(self):
        path = ROOT / 'tools/data/skill-ap-cost-overrides.json'
        self.assertTrue(path.is_file())


class SkillEffectConditionsDataTests(unittest.TestCase):
    def test_conditions_json_loads(self):
        import json
        doc = json.loads((ROOT / 'tools/data/skill-effect-conditions.json').read_text(encoding='utf-8'))
        self.assertIn('skills', doc)
