import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import expedition_data  # noqa: E402


class ExpeditionDataTests(unittest.TestCase):
    def test_expedition_json_has_party_ticket_stub(self):
        expedition_data.build()
        data = json.loads((ROOT / 'tools/data/expedition.json').read_text(encoding='utf-8'))
        ticket = data['tickets']['4']
        self.assertEqual(ticket['max_count'], 30)
        self.assertIn('Party', ticket['dungeon_types'])
        self.assertIn('expeditions', data)
        self.assertIn('600002', data['expeditions'])
        self.assertEqual(data['expeditions']['600002']['levels'][0]['dungeon_id'], 600002)


if __name__ == '__main__':
    unittest.main()
