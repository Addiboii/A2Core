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

    def test_bomb_box_carries_its_abnormal_group(self):
        rows = world_objects.envobj_rows({'Properties': [
            {'ID': {'Value': 1400823}, 'Name': 'E_L0_Layer_2_BombBox_01', 'Usage': 'EEnvObjectUsage::Normal', 'InteractRange': 80,
             'InteractCount': 1, 'FinishDuration': 10000, 'bDespawn': True,
             'InteractConditionList': [{'ConditionType': 'EEnvObjInteractConditionType::NotHaveAbnormalGroup', 'Value': 1196}]},
            {'ID': {'Value': 2}, 'Name': 'Plain', 'Usage': 'EEnvObjectUsage::Normal'}]})
        self.assertEqual(rows[1400823]['not_have_abnormal_group'], 1196)
        self.assertEqual(rows[2]['not_have_abnormal_group'], 0)
        spawn = {'SpawnInfoList': [{'Name': 'Box', 'EnvObjIdList': [{'Value': 1400823}, {'Value': 2}],
                                    'Positions': [{'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'Rotation': {'Yaw': 0}}]}]}
        placed = world_objects.objects(spawn, rows, wanted=set(), everything=True)
        self.assertEqual(placed[0]['carry'], {'group': 1196, 'despawn': True, 'finish_duration': 10000})
        self.assertNotIn('carry', placed[1])

    def test_combat_power_interact_condition_is_exported(self):
        rows = world_objects.envobj_rows({'Properties': [
            {'ID': {'Value': 1200602}, 'Name': 'E_L1_Layer_HQ004_EtheriaCrack_1', 'Usage': 'EEnvObjectUsage::Normal',
             'InteractConditionList': [{'ConditionType': 'EEnvObjInteractConditionType::CombatPower', 'Value': 3200}]}]})
        self.assertEqual(rows[1200602]['required_combat_power'], 3200)

    def test_enter_door_with_an_unserved_target_uses_the_layer_named_after_it(self):
        dungeons = {'2': {'name': 'World_L_A_Layer_01', 'type': 'InstanceLayer'},
                    '23': {'name': 'Abyss_Reshanta_C', 'type': 'Abyss'},
                    '51': {'name': 'World_L_A_Layer_CommandPost_01', 'type': 'InstanceLayer'}}
        rows = {1200002: {'name': 'World_L_A_Layer_01_EnterDoor', 'usage': 'EnterInstanceLayer', 'usage_value': '23'},
                1200006: {'name': 'EnvObj_Dawn_CP_EnterDoor_1', 'usage': 'EnterInstanceLayer', 'usage_value': '51'},
                1: {'name': 'Unknown_EnterDoor', 'usage': 'EnterDungeon', 'usage_value': '23'}}
        portals = world_objects.party_portals(rows, dungeons)
        self.assertEqual(portals, {1200002: [2], 1200006: [51]})

    def test_generated_portals_only_enter_served_dungeons(self):
        events = json.loads((ROOT / 'tools/data/map-events.json').read_text(encoding='utf-8'))
        served = set(events['served_maps'])
        maps = json.loads((ROOT / 'tools/data/world-objects.json').read_text(encoding='utf-8'))['maps']
        for map_id, entry in maps.items():
            for obj in entry['objects']:
                for dungeon in obj.get('portal') or []:
                    self.assertIn(events['dungeons'][str(dungeon)]['map'], served, f"{obj['name']} on {map_id} enters dungeon {dungeon}")
        door = next(o for o in maps['1010']['objects'] if o['name'] == 'World_L_A_Layer_01_EnterDoor')
        self.assertEqual(door['portal'], [2])

    def test_generated_poeta(self):
        data = json.loads((ROOT / 'tools/data/world-objects.json').read_text(encoding='utf-8'))['maps']['1000']
        self.assertIn(1200530, [o['env'] for o in data['objects']])
        self.assertIn('Trigger_Q_Clione2', [t['name'] for t in data['triggers']])


if __name__ == '__main__':
    unittest.main()
