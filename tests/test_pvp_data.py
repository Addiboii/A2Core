#!/usr/bin/env python3
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PVP = ROOT / 'tools/data/pvp.json'


class PvpDataTests(unittest.TestCase):
    def test_pvp_json_has_invasion_maps_and_pin(self):
        data = json.loads(PVP.read_text(encoding='utf-8'))
        self.assertEqual(25767555, data['pin']['steam_build'])
        self.assertIn(500032, data['invasion_maps'])
        self.assertEqual(20, data['protect_below_level'])


if __name__ == '__main__':
    unittest.main()
