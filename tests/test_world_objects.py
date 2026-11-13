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

    def test_recovery_orbs_carry_life_time_radius_and_hit_skills(self):
        rows = world_objects.envobj_rows({'Properties': [
            {'ID': {'Value': 7}, 'Name': 'Basic_RecoveryOrb_01', 'Usage': 'EEnvObjectUsage::Overlap', 'LifeTime': 30000,
             'CollisionCylinderRadius': 45, 'HitSkillIdList': [{'Value': 1801510}, {'Value': 1801520}]},
            {'ID': {'Value': 8}, 'Name': 'Box', 'Usage': 'EEnvObjectUsage::Quest'}]})
        spawn = {'SpawnInfoList': [{'Name': n, 'EnvObjIdList': [{'Value': e}],
                                    'Positions': [{'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'Rotation': {'Yaw': 0}}]} for n, e in (('o', 7), ('b', 8))]}
        orb, box = world_objects.objects(spawn, rows, wanted=set(), everything=True)
        self.assertEqual(orb['recovery_orb'], {'life_time': 30000, 'radius': 45, 'hit_skills': [1801510, 1801520]})
        self.assertNotIn('recovery_orb', box)
        self.assertEqual(world_objects.recovery_orbs(rows), {'7': {'name': 'Basic_RecoveryOrb_01', 'life_time': 30000, 'radius': 45,
                                                                   'hit_skills': [1801510, 1801520]}})
        data = json.loads((ROOT / 'tools/data/world-objects.json').read_text(encoding='utf-8'))
        self.assertEqual(data['recovery_orbs']['1100133']['life_time'], 30000)
        self.assertEqual(sum('recovery_orb' in o for m in data['maps'].values() for o in m['objects']), 104)

    def test_treasure_boxes_link_their_table_by_name(self):
        import tempfile
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / 'table-001.json').write_text(json.dumps({'Properties': {'Data': [
                {'Name': 'reward_manual_FieldEvent_L_TreasureBox_1100312'}, {'Name': 'reward_manual_TreasureBox_L_Normal'},
                {'Name': 'reward_manual_FieldEvent_L_TreasureBox_x'}]}}), encoding='utf-8')
            chests = world_objects.chest_tables(Path(folder))
        self.assertEqual(chests, {1100312: 'reward_manual_FieldEvent_L_TreasureBox_1100312'})
        rows = world_objects.envobj_rows({'Properties': [{'ID': {'Value': 1100312}, 'Name': 'E_L1_FE_TH_S2_TreasureBox_01',
                                                          'Usage': 'EEnvObjectUsage::Normal', 'InteractTime': 2000}]})
        spawn = {'SpawnInfoList': [{'Name': 'FE_TH_S2_Box_1', 'EnvObjIdList': [{'Value': 1100312}],
                                    'Positions': [{'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'Rotation': {'Yaw': 0}}]}]}
        box, = world_objects.objects(spawn, rows, wanted=set(), everything=True, chests=chests)
        self.assertEqual(box['loot'], 'reward_manual_FieldEvent_L_TreasureBox_1100312')
        data = json.loads((ROOT / 'tools/data/world-objects.json').read_text(encoding='utf-8'))
        linked = {o['env'] for m in data['maps'].values() for o in m['objects'] if 'loot' in o}
        self.assertEqual(linked, {1100311, 1100312, 1200018, 1200019, 1200020})

    def test_every_placed_object_has_its_envobjdata_name(self):
        data = json.loads((ROOT / 'tools/data/world-objects.json').read_text(encoding='utf-8'))
        names = data['env_names']
        # Munihelle's second firewood is spawned as E_D0_FireWood_001_2 but is EnvObjData E_D0_FireWood_002.
        fire = {o['name']: names.get(str(o['env'])) for o in data['maps']['110002']['objects'] if 'FireWood' in o['name']}
        self.assertEqual(fire, {'E_D0_FireWood_001_1': 'E_D0_FireWood_001', 'E_D0_FireWood_001_2': 'E_D0_FireWood_002'})
        placed = {o['env'] for m in data['maps'].values() for o in m['objects']}
        self.assertTrue({str(e) for e in placed} >= set(names) and len(names) > 0.95 * len(placed))

    def test_doors(self):
        dungeons = {'2': {'name': 'World_L_A_Layer_01', 'type': 'InstanceLayer'}, '23': {'name': 'Abyss_Reshanta_C', 'type': 'Abyss'}}
        rows = world_objects.envobj_rows({'Properties': [
            {'ID': {'Value': 1}, 'Name': 'E_L1_Barracks_LayerEnterDoor_001', 'Usage': 'EEnvObjectUsage::EnterInstanceLayer', 'UsageValue': chr(23)},
            {'ID': {'Value': 2}, 'Name': 'E_D1_Exit_01', 'Usage': 'EEnvObjectUsage::MoveMap', 'UsageValue': 'Garrison_Out'}]})
        # A door naming an unserved map (23, a field map) enters the layer its spawn entry is named after.
        self.assertEqual(world_objects.party_portals(rows, dungeons), {})
        self.assertEqual(world_objects.door_layer('World_L_A_Layer_01_EnterDoor', dungeons), 2)
        spawn = {'SpawnInfoList': [
            {'Name': 'World_L_A_Layer_01_EnterDoor', 'EnvObjIdList': [{'Value': 1}], 'Positions': [{'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'Rotation': {'Yaw': 0}}]},
            {'Name': 'Exit', 'EnvObjIdList': [{'Value': 2}], 'Positions': [{'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'Rotation': {'Yaw': 0}}]}]}
        placed = world_objects.objects(spawn, rows, wanted=set(), everything=True, dungeons=dungeons)
        self.assertEqual([(p.get('portal'), p.get('move_to')) for p in placed], [([2], None), (None, 'Garrison_Out')])
        data = json.loads((ROOT / 'tools/data/world-objects.json').read_text(encoding='utf-8'))
        door = [o for o in data['maps']['1010']['objects'] if o['name'] == 'World_L_A_Layer_01_EnterDoor']
        self.assertEqual([o['portal'] for o in door], [[2]])

    def test_destructible_carry_and_gated_objects(self):
        rows = world_objects.envobj_rows({'Properties': [
            {'ID': {'Value': 1400824}, 'Name': 'E_L0_Layer_2_Tower_01', 'Usage': 'EEnvObjectUsage::Normal', 'HitLimit': 5,
             'HitSkillIdList': [{'Value': 5003}], 'RelationshipEntity': 'ERelationshipEntity::EnvObj_Destroy'},
            {'ID': {'Value': 1400823}, 'Name': 'E_L0_Layer_2_BombBox_01', 'Usage': 'EEnvObjectUsage::Normal', 'FinishDuration': 10000,
             'bDespawn': True, 'InteractConditionList': [
                 {'ConditionType': 'EEnvObjCanInteractType::NotHaveAbnormalGroup', 'ConditionValues': ['1196']}]},
            {'ID': {'Value': 1300001}, 'Name': 'E_L1_VerteronToEltnen_Portal', 'Usage': 'EEnvObjectUsage::MovemapCondition',
             'CombatPower': 270000, 'Level': 45, 'ItemLevel': 3300}]})
        self.assertEqual(rows[1400823]['not_have_abnormal_group'], 1196)
        spawn = {'SpawnInfoList': [
            {'Name': n, 'EnvObjIdList': [{'Value': e}], 'Positions': [{'Location': {'X': 0, 'Y': 0, 'Z': 0}, 'Rotation': {'Yaw': 0}}]}
            for n, e in (('Tower', 1400824), ('Box', 1400823), ('Portal', 1300001))]}
        tower, box, portal = world_objects.objects(spawn, rows, wanted=set(), everything=True)
        self.assertEqual((tower['hit_limit'], tower['hit_skills'], tower['relationship']), (5, [5003], 15))
        self.assertEqual(box['carry'], {'group': 1196, 'despawn': True, 'finish_duration': 10000})
        self.assertEqual((portal['required_combat_power'], portal['min_level'], portal['item_level']), (270000, 45, 3300))

    def test_generated_poeta(self):
        data = json.loads((ROOT / 'tools/data/world-objects.json').read_text(encoding='utf-8'))['maps']['1000']
        self.assertIn(1200530, [o['env'] for o in data['objects']])
        self.assertIn('Trigger_Q_Clione2', [t['name'] for t in data['triggers']])


if __name__ == '__main__':
    unittest.main()
