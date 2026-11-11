import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import map_events  # noqa: E402


class MapEventTests(unittest.TestCase):
    def test_decode_is_the_xor_mask_over_json(self):
        plain = json.dumps({'MapID': 5, 'Name': 'X_MapEvent', 'Stages': []}).encode('utf-8')
        masked = bytes(b ^ map_events.XOR_KEY[i % 4] for i, b in enumerate(plain))
        self.assertEqual(map_events.decode(masked)['Name'], 'X_MapEvent')

    def test_convert_keeps_stages_directives_and_resolves_npcs(self):
        event = {
            'MapID': 100010, 'Name': 'T_MapEvent',
            'StartConditions': [[{'Type': 'IncompleteQuestStep', 'Value': ['110101202']}]],
            'FailConditions': [], 'GlobalConditions': [],
            'Stages': [{'Id': 1, 'Key': '', 'FailConditions': [], 'Steps': [{
                'Id': 1,
                'PreDirectives': [
                    {'Id': 1, 'Type': 'StartSpawner', 'value_list': [{'value': 'N_Robin'}, {'value': '0'}], 'LoopCount': 1, 'LoopInterval': 0},
                    {'Id': 2, 'Type': 'PlayHurryNpcTalk', 'value_list': [{'value': 'Talk_1'}], 'LoopCount': 0, 'LoopInterval': 15000,
                     'Conditions': [[{'Type': 'ObjectiveIncomplete', 'Value': ['PCCombat']}]]}],
                'Objectives': [
                    {'Id': 1, 'Type': 'PCCombatStart', 'Optional': True, 'Key': 'PCCombat'},
                    {'Id': 2, 'Type': 'KillNpc', 'value_list': [{'value': 'M_A, M_B'}, {'value': '3'}, {'value': ''}],
                     'PostDirectives': [{'Id': 3, 'Type': 'Delay', 'value_list': [{'value': '3000'}], 'LoopCount': 1, 'LoopInterval': 0}]}],
                'PostDirectives': [{'Id': 4, 'Type': 'DungeonClear', 'LoopCount': 1, 'LoopInterval': 0}]}]}],
        }
        out = map_events.convert(event, {'M_A': 11, 'M_B': 12})
        self.assertEqual(out['map'], 100010)
        self.assertEqual(out['start'], [[{'t': 'IncompleteQuestStep', 'v': ['110101202']}]])
        step = out['stages'][0]['steps'][0]
        self.assertEqual(step['pre'][0], {'id': 1, 't': 'StartSpawner', 'v': ['N_Robin', '0']})
        self.assertEqual(step['pre'][1]['n'], 0)
        self.assertEqual(step['pre'][1]['i'], 15000)
        self.assertEqual(step['pre'][1]['c'], [[{'t': 'ObjectiveIncomplete', 'v': ['PCCombat']}]])
        self.assertEqual(step['obj'][0], {'id': 1, 't': 'PCCombatStart', 'v': [], 'opt': True, 'key': 'PCCombat'})
        self.assertEqual(step['obj'][1]['npcs'], [11, 12])
        self.assertEqual(step['obj'][1]['post'][0]['v'], ['3000'])
        self.assertEqual(step['post'][0]['t'], 'DungeonClear')
        self.assertEqual(map_events.directive_spawners(event), {'N_Robin'})

    def test_generated_data_carries_the_expedition_rules(self):
        data = json.loads((ROOT / 'tools/data/map-events.json').read_text(encoding='utf-8'))
        krao = data['dungeons']['600002']
        self.assertEqual(krao['type'], 'Party')
        self.assertIn(600002, data['served_maps'])
        self.assertEqual((krao['limit'], krao['hold']), (3600, 600))
        self.assertEqual((krao['min_level'], krao['item_level'], krao['max_member']), (20, 200, 5))
        self.assertEqual(krao['cube_boss'], 2310171)
        self.assertEqual(krao['cube']['2310171'][1], [930100001, 100000000, 25000, 25000])
        seal = data['dungeons']['310001']
        self.assertEqual((seal['type'], seal['hold'], seal['max_member']), ('Seal', 10, 1))
        self.assertIn(310001, data['served_maps'])
        self.assertEqual(seal['quest_gate'], [['EQ1810001', 1]])
        self.assertEqual(seal['reward_light'][0], [930100001, 15000])

    def test_generated_data_carries_the_revive_limits(self):
        data = json.loads((ROOT / 'tools/data/map-events.json').read_text(encoding='utf-8'))
        limits = {data['dungeons'][d]['name']: data['dungeons'][d]['rebirth_limit'] for d in data['dungeons']
                  if 'rebirth_limit' in data['dungeons'][d]}
        self.assertEqual(limits, {'KraoCave_Hard': 3, 'Urugugu_Canyon_Hard': 3, 'Draupnir_Hard': 3, 'BakronIsland_Hard': 3,
                                  'ReforgedAbyss_Normal': 2, 'ReforgedAbyss_Hard': 2, 'ErosionPurifier_01': 2,
                                  'MusphelHolyGrail_01': 2, 'MusphelHolyGrail_02': 2})
        self.assertNotIn('rebirth_limit', data['dungeons']['600002'])   # KraoCave_Normal: no limit

    def test_generated_data_serves_the_poeta_layers(self):
        data = json.loads((ROOT / 'tools/data/map-events.json').read_text(encoding='utf-8'))
        self.assertIn(100010, data['served_maps'])
        self.assertEqual(data['dungeons']['96']['map'], 100010)
        self.assertEqual(data['npc_entries']['2930710']['dungeon'], 96)
        self.assertIn('N_L0_Starter_HQ001_Robin_01', data['deferred']['100010'])
        event = data['events']['World_L_Starter_HQ001_MapEvent']
        self.assertEqual(len(event['stages'][0]['steps']), 8)
        self.assertEqual(event['stages'][0]['steps'][7]['post'][-1]['t'], 'MapExit')

    def test_renamed_spawn_entries_resolve_to_what_they_became(self):
        spawns = {'Od_1', 'Od_2', 'Od_10', 'FE_BS_D_02_Mob_1', 'Kept', 'Twin_A_X', 'Twin_B_X'}
        self.assertEqual(map_events.resolve_spawner('Kept', spawns), ['Kept'])
        self.assertEqual(map_events.resolve_spawner('Od', spawns), ['Od_1', 'Od_2', 'Od_10'])
        self.assertEqual(map_events.resolve_spawner('FE_BS_02_Mob_1', spawns), ['FE_BS_D_02_Mob_1'])
        self.assertEqual(map_events.resolve_spawner('Twin_X', spawns), ['Twin_X'])   # ambiguous: left alone
        self.assertEqual(map_events.resolve_spawner('Gone', spawns), ['Gone'])
        event = {'Stages': [{'Steps': [{'PreDirectives': [
            {'Type': 'StartSpawner', 'value_list': [{'value': 'Od, Kept'}, {'value': '0'}]},
            {'Type': 'DespawnAll', 'value_list': [{'value': 'Kept'}, {'value': 'FE_BS_02_Mob_1'}]},
            {'Type': 'AddAbnormal', 'value_list': [{'value': '1'}, {'value': '-1'}, {'value': 'Od'}]}]}]}]}
        renamed = map_events.rename_spawners(event, spawns)
        directives = event['Stages'][0]['Steps'][0]['PreDirectives']
        self.assertEqual(directives[0]['value_list'][0]['value'], 'Od_1,Od_2,Od_10,Kept')
        self.assertEqual(directives[1]['value_list'][1]['value'], 'FE_BS_D_02_Mob_1')
        self.assertEqual(directives[2]['value_list'][2]['value'], 'Od')
        self.assertEqual(renamed, {'Od': ['Od_1', 'Od_2', 'Od_10'], 'FE_BS_02_Mob_1': ['FE_BS_D_02_Mob_1']})

    def test_generated_data_resolves_the_dark_broken_sword_spawns(self):
        data = json.loads((ROOT / 'tools/data/map-events.json').read_text(encoding='utf-8'))
        self.assertIn('FE_BS_D_02_Normal_Mob_01_1', data['deferred']['510013'])
        self.assertEqual(data['renamed']['Event_BrokenSword_02_D_MapEvent']['FE_BS_02_Normal_Mob_01_1'], ['FE_BS_D_02_Normal_Mob_01_1'])


if __name__ == '__main__':
    unittest.main()
