import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import lobby_zones  # noqa: E402


class LobbyZoneTests(unittest.TestCase):
    def test_defaults_name_existing_subzones_only(self):
        maps = [{'ID': {'Value': 1000}, 'DefaultSubzoneID': 3000}, {'ID': {'Value': 1110}, 'DefaultSubzoneID': 1009}]
        subzones = [{'ID': {'Value': 3000}}, {'ID': {'Value': 1000}}]
        self.assertEqual(lobby_zones.defaults(maps, subzones), {'1000': 3000})

    def test_data_file_has_the_starter_maps(self):
        data = json.loads(lobby_zones.OUTPUT.read_text(encoding='utf-8'))['defaults']
        self.assertEqual((data['1000'], data['1100'], data['100001'], data['110001']), (3000, 4000, 3014, 4003))


if __name__ == '__main__':
    unittest.main()
