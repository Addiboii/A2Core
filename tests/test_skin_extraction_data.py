import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class SkinExtractionDataTests(unittest.TestCase):
    def test_committed_json_has_test_template(self):
        data = json.loads((ROOT / 'tools/data/skin_extraction.json').read_text(encoding='utf-8'))
        rule = data['items']['630543004']
        self.assertEqual([30001101], rule['skins'])
        self.assertEqual([630543005, 1], rule['material'])

    def test_build_without_local_tables(self):
        import sys
        sys.path.insert(0, str(ROOT / 'tools'))
        import skin_extraction_data as sed
        built = sed.build()
        self.assertIn('630543004', built['items'])


if __name__ == '__main__':
    unittest.main()
