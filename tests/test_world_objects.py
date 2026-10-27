import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import world_objects  # noqa: E402


class WorldObjectTests(unittest.TestCase):
    def test_objects_and_triggers_are_filtered(self):
        rows = world_objects.envobj_rows({'Properties': [
            {'ID': {'Value': 1}, 'Name': 'Box', 'Usage': 'EEnvObjectUsage::Quest', 'InteractTime': 2500, 'InteractRange': 200,
             'RelationshipEntity': 'ERelationshipEntity::EnvObj_Neutral'},
            {'ID': {'Value': 2}, 'Name': 'Wall', 'Usage': 'EEnvObjectUsage::None'},
            {'ID': {'Value': 3}, 'Name': 'Stone', 'Usage': 'EEnvObjectUsage::TeleportArtifact'}]})
        self.assertEqual(rows[1]['relationship'], 7)
        spawn = {'SpawnInfoList': [
            {'Name': 'a', 'UniqueKey': 5, 'EnvObjIdList': [{'Value': 1}],
             'Positions': [{'Location': {'X': 1, 'Y': 2, 'Z': 3}, 'Rotation': {'Yaw': 90.0}}]},
            {'Name': 'b', 'EnvObjIdList': [{'Value': 2}], 'Positions': [{'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'Rotation': {'Yaw': 0}}]},
            {'Name': 'c', 'EnvObjIdList': [{'Value': 3}], 'Positions': [{'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'Rotation': {'Yaw': 0}}]}]}
        placed = world_objects.objects(spawn, rows, wanted={2})
        self.assertEqual([p['env'] for p in placed], [1, 2])   # quest usage, quest-named; never teleport artifacts
        areas = world_objects.triggers({'TriggerActorDataMap': [
            {'Value': {'Name': 'Trigger_Q_A', 'Location': {'X': 1, 'Y': 2, 'Z': 3}, 'Direction': 10.0,
                       'CollisionType': 'ETriggerActorCollisionType::Box', 'BoxSize': {'X': 100, 'Y': 50}, 'Height': 300,
                       'FuncType': 'ETriggerActorFuncType::Quest'}},
            {'Value': {'Name': 'Sound', 'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'FuncType': 'ETriggerActorFuncType::Sound'}}]}, wanted=set())
        self.assertEqual([(a['name'], a['shape'], a['box']) for a in areas], [('Trigger_Q_A', 'Box', [100, 50])])

    def test_generated_poeta(self):
        data = json.loads((ROOT / 'tools/data/world-objects.json').read_text(encoding='utf-8'))['maps']['1000']
        self.assertIn(1200530, [o['env'] for o in data['objects']])
        self.assertIn('Trigger_Q_Clione2', [t['name'] for t in data['triggers']])


if __name__ == '__main__':
    unittest.main()
