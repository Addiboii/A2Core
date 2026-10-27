import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import teleport_artifacts  # noqa: E402


class TeleportArtifactTests(unittest.TestCase):
    def test_placements_join_mapdata_with_envobj_usage(self):
        objects = teleport_artifacts.artifact_objects({'Properties': [
            {'ID': {'Value': 1200081}, 'Usage': 'EEnvObjectUsage::TeleportArtifact', 'UsageValue': 'TeleportArtifact_1051035'},
            {'ID': {'Value': 1200001}, 'Usage': 'EEnvObjectUsage::None', 'UsageValue': 'None'}]})
        self.assertEqual(objects, {1200081: 'TeleportArtifact_1051035'})
        mapdata = {'SpawnInfoList': [
            {'UniqueKey': 794868238, 'Name': 'EnvObj_L0_R3_TeleportArtifact_1', 'EnvObjIdList': [{'Value': 1200081}],
             'Positions': [{'Location': {'X': -5970.18017578125, 'Y': -16702.427734375, 'Z': 20292.84765625},
                            'Rotation': {'Pitch': 0.0, 'Yaw': 74.53125, 'Roll': 0.0}}]},
            {'UniqueKey': 1, 'Name': 'Bonfire', 'EnvObjIdList': [{'Value': 1200001}], 'Positions': []}]}
        found = teleport_artifacts.placements(mapdata, objects)
        self.assertEqual(found, [{'name': 'EnvObj_L0_R3_TeleportArtifact_1', 'env_obj': 1200081,
                                  'artifact': 'TeleportArtifact_1051035', 'artifact_id': 1051035,
                                  'unique_key': 794868238, 'position': [-5970.18, -16702.43, 20292.85], 'yaw': 74.5312}])

    def test_generated_poeta_kibelisk(self):
        data = json.loads((ROOT / 'tools/data/teleport-artifacts.json').read_text(encoding='utf-8'))
        self.assertEqual([k['artifact_id'] for k in data['maps']['1000']], [1051035])


if __name__ == '__main__':
    unittest.main()
