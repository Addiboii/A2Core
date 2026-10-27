import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import subzones  # noqa: E402


class SubzoneTests(unittest.TestCase):
    def test_fog_group_names(self):
        self.assertEqual(subzones.fog_group('ESubzoneGroupID::ESubzoneGroupID_64'), 64)
        self.assertEqual(subzones.fog_group('ESubzoneGroupID::ESubzoneGroupID_None'), 0)
        self.assertEqual(subzones.fog_group(None), 0)

    def test_volumes_join_mapdata_and_subzone_table(self):
        rows = subzones.subzone_rows({'Properties': {'Data': [
            {'ID': {'Value': 3011}, 'Priority': 5, 'WorldMapFogGroupId': 'ESubzoneGroupID::ESubzoneGroupID_65'},
            {'ID': {'Value': 3012}, 'Priority': 5, 'WorldMapFogGroupId': 'ESubzoneGroupID::ESubzoneGroupID_None'}]}})
        self.assertEqual(rows, {3011: (65, 5), 3012: (0, 5)})
        mapdata = {'Properties': {'Data': {'MapId': 1000, 'SubzoneVolumeInfoMap': [
            {'Key': 'L_Starter_Subzone_Base', 'Value': {'LabelName': 'L_Starter_Subzone_Base', 'SubzoneTableId': 3011,
             'HeightMin': 100.0, 'HeightMax': 200.0, 'Points': [{'X': 0, 'Y': 0}, {'X': 10, 'Y': 0}, {'X': 10, 'Y': 10}]}},
            {'Key': 'Other', 'Value': {'SubzoneTableId': 3012, 'Points': [{'X': 0, 'Y': 0}]}}]}}}
        map_id, areas = subzones.volumes(mapdata, rows)
        self.assertEqual(map_id, 1000)
        self.assertEqual(areas[0], {'subzone': 3011, 'name': 'L_Starter_Subzone_Base', 'group': 65, 'priority': 5,
                                    'z': [100.0, 200.0], 'points': [[0, 0], [10, 0], [10, 10]]})
        self.assertEqual((areas[1]['name'], areas[1]['group']), ('Other', 0))   # no fog group: still an EnterSubZone target

    def test_generated_poeta_groups(self):
        data = json.loads((ROOT / 'tools/data/subzones.json').read_text(encoding='utf-8'))
        self.assertEqual(sorted({a['group'] for a in data['maps']['1000']} - {0}), [63, 64, 65, 66])


if __name__ == '__main__':
    unittest.main()
