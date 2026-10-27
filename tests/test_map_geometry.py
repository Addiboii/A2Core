import json
import unittest

from tools import map_geometry


def row(map_id, name, base_dir, base=0):
    return {'ID': {'Value': map_id}, 'Name': name, 'BaseDir': base_dir, 'BaseMapId': {'Value': base}}


class MapGeometryTests(unittest.TestCase):
    MAPS = [row(1000, 'World_L_Starter', 'World/World_L'), row(904, 'L2_Agit_1', 'World/World_L_B/InstanceLayer', 1011),
            row(1011, 'World_L_B', 'World/World_L/'), row(5, 'NoDir', '')]

    def test_maps_with_their_own_geometry_and_package_dirs(self):
        self.assertEqual(map_geometry.geometry_maps(self.MAPS), [
            {'id': 1000, 'name': 'World_L_Starter', 'dir': 'AION2/Content/Map/World/World_L/World_L_Starter'},
            {'id': 1011, 'name': 'World_L_B', 'dir': 'AION2/Content/Map/World/World_L/World_L_B'}])

    def test_instances_use_their_base_map(self):
        self.assertEqual(map_geometry.bases(self.MAPS), {'904': 1011})

    def test_embedded_bases_match_the_server_data(self):
        data = json.loads(map_geometry.BASES.read_text(encoding='utf-8'))
        self.assertEqual(data['bases']['904'], 1011)
        self.assertNotIn('1000', data['bases'])


if __name__ == '__main__':
    unittest.main()
