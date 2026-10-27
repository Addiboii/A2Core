import json
import unittest
from pathlib import Path

DATA = json.loads((Path(__file__).resolve().parents[1] / 'tools/data/boxes.json').read_text(encoding='utf-8'))


class BoxDataTests(unittest.TestCase):
    def test_every_chest_has_its_box_and_every_package_resolves(self):
        for item, (effect, _auto, *values) in DATA['uses'].items():
            if effect == 'RewardBox':
                self.assertIn(values[0], DATA['boxes'], item)
        for box in DATA['boxes'].values():
            for package, probability, count in box['packages']:
                self.assertIn(package, DATA['packages'])
                self.assertTrue(0 < probability <= 10000 and count >= 1)

    def test_pet_items_grant_known_rides(self):
        for item, (effect, auto, *values) in DATA['uses'].items():
            if effect == 'GetVehicle':
                self.assertEqual(auto, 1, item)
                self.assertEqual(DATA['rides'][values[0]]['item'], int(item))

    def test_map_event_abnormals_are_known(self):
        # The story layers' AddAbnormal ids (tools/map_events.py): the stun-and-immunity 10000017, the Krall transformation 44000501.
        self.assertEqual([e[1] for e in DATA['abnormals']['10000017']['effects']], ['BlockTargeting', 'BlockTargetSkill', 'ImmuneSkillEffectGroup', 'Stun'])
        self.assertIn('Polymorph', [e[1] for e in DATA['abnormals']['44000501']['effects']])
        self.assertIn(['ChangeRelationshipEntity', 'TRUE', 'PC_FriendlyToAll', 'TRUE'], [e[1:5] for e in DATA['abnormals']['44000501']['effects']])

    def test_title_and_skin_items_name_their_rewards(self):
        self.assertEqual(DATA['uses']['502530001'][:3], ['GetTitle', 0, '19033001'])
        self.assertEqual(DATA['uses']['515810008'][:3], ['GetSkin', 0, 'AR_0067_T05_Torso'])
        self.assertEqual(DATA['uses']['535510001'][:3], ['GetSkinSet', 1, 'AR_0001_T04'])
        root = Path(__file__).resolve().parents[1]
        titles = json.loads((root / 'tools/data/titles.json').read_text(encoding='utf-8'))['titles']
        self.assertEqual(titles['11010002'], [1, 3, [['HPMax', 300]], [['Critical', 5]]])
        skins = json.loads((root / 'tools/data/skins.json').read_text(encoding='utf-8'))
        self.assertEqual(skins['skins']['AR_0067_T05_Torso'], 30067101)
        self.assertEqual(len(skins['sets']['AR_0001_T04']), 7)

    def test_cogni_pet_chest_holds_41_equally_likely_pets(self):
        self.assertEqual(DATA['uses']['533700117'][:3], ['RewardBox', 0, 'Box_Vehicle_A_06_b'])
        pets = DATA['packages']['Box_Package_Vehicle_A_06_b']
        self.assertEqual(len(pets), 41)
        self.assertEqual(sum(p[1] for p in pets), 100_000_000)
        self.assertTrue(all(DATA['uses'][str(p[0])][0] == 'GetVehicle' for p in pets))


class LevelDataTests(unittest.TestCase):
    def test_need_exp_matches_the_official_level_ups(self):
        need = json.loads((Path(__file__).resolve().parents[1] / 'tools/data/levels.json').read_text(encoding='utf-8'))['need']
        # Official quest level-ups (capture 20261013-144836): rewards 1133, 1712, 2589 each took exactly one level.
        self.assertEqual([need['1'], need['2'], need['3']], [1133, 1712, 2589])
        self.assertEqual(len(need), 100)


class LootDataTests(unittest.TestCase):
    def test_tables_resolve_and_poeta_has_no_loot(self):
        root = Path(__file__).resolve().parents[1]
        loot = json.loads((root / 'tools/data/loot.json').read_text(encoding='utf-8'))
        for names in loot['npcs'].values():
            for name in names:
                self.assertIn(name, loot['tables'])
        for table in loot['tables'].values():
            for package, chance, count in table['packages']:
                self.assertIn(package, loot['packages'])
        # The Stone Elemental (level 10) drops its pet soul at 100%; Poeta's prologue monsters have no loot.
        stone = [i for n in loot['npcs']['2100002'] for i in loot['tables'][n]['items']]
        self.assertIn([534660085, 100_000_000, 1, 1], stone)
        self.assertNotIn('2101063', loot['npcs'])


if __name__ == '__main__':
    unittest.main()

    def test_dismantling_data_names_known_items_and_packages(self):
        data = json.loads((ROOT / 'tools/data/decompose.json').read_text(encoding='utf-8'))
        sword = data['items']['110160001']
        self.assertEqual(sword['c'], 1000)
        self.assertEqual(len(sword['g']), 2)
        for group in sword['g']:
            rows = data['groups'][group]
            self.assertTrue(rows and all(p['n'] in data['packages'] for row in rows for p in row['p']))
        self.assertTrue(all(len(e) == 4 and e[1] > 0 for items in data['packages'].values() for e in items))

    def test_achievement_data_carries_objectives_steps_and_rewards(self):
        data = json.loads((ROOT / 'tools/data/achievements.json').read_text(encoding='utf-8'))
        krao = data['achievements']['100030000']
        self.assertEqual((krao['t'], krao['v'], krao['s']), ('ClearDungeon', ['KraoCave_Easy'], [[1, 1]]))
        board = data['achievements']['510320100']
        self.assertEqual(board['t'], 'Attendance')
        self.assertEqual(len(board['s']), 7)
        self.assertEqual(board['r']['1'], [[930100001, 50000]])
        self.assertEqual(data['groups'][board['g']]['c'], 'Attendance')
        self.assertTrue(data['attendance']['1022']['from'] < data['attendance']['1022']['to'])

    def test_battle_pass_groups_link_passes_and_missions(self):
        data = json.loads((ROOT / 'tools/data/achievements.json').read_text(encoding='utf-8'))
        group = data['battlepass']['10044']
        self.assertEqual(group['missions'], 'Global_BattlePass_Mission_Light_S1_1')
        self.assertEqual([p[1] for p in group['passes']], [810433000, 810434000])
        self.assertEqual(data['achievements']['910430800']['p'], {'1': 30})
        self.assertEqual(data['achievements']['810433000']['t'], 'BattlePassExp')

    def test_collection_data_lists_targets_and_rewards(self):
        data = json.loads((ROOT / 'tools/data/collections.json').read_text(encoding='utf-8'))
        daily = data['collections']['5401317']
        self.assertEqual((daily['week'], daily['kind'], daily['day'], daily['t'], daily['r']), (13, 1, 7, 1, 1))
        lists = data['lists']['Collection_Light_Season01_Daily']
        self.assertEqual(len(lists), 143)
        self.assertEqual(lists['1011001']['n'], 3)
        self.assertEqual(lists['1011001']['rewards'][0], [930100003, 850])

    def test_boss_challenge_data_places_bosses_with_rewards(self):
        data = json.loads((ROOT / 'tools/data/bosschallenge.json').read_text(encoding='utf-8'))
        self.assertEqual((data['dungeon'], data['map']), (200003, 200003))
        self.assertEqual(len(data['spawn']), 4)
        first = data['contents']['BossChallenge_1001']['1']
        self.assertEqual(first['npc'], 2980040)
        self.assertEqual((first['first'], first['repeat']), ([[930100017, 500]], [[930100017, 100]]))
        self.assertEqual(data['groups']['1001']['max'], 10)
        npcs = json.loads((ROOT / 'tools/data/world-npcs.json').read_text(encoding='utf-8'))['templates']
        self.assertIn('2980040', npcs)

