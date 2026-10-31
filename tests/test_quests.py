import json
import tempfile
import unittest
from pathlib import Path

from tools import quests


def quest_row(name, qid, **extra):
    row = {'ID': {'Value': qid}, 'Name': name, 'Race': 'ERace::Light', 'UnlockLevel': 1, 'RecommendedLevel': 2,
           'QuestText': 'STR_' + name, 'Type': 'EQuestType::Hero', 'Part': 'hero_poeta_01',
           'AcquirableConditions': [], 'AcquireType': 'EQuestAcquireType::None', 'AcquireValue': 'None',
           'AcquireMapId': {'Value': 1000}, 'AcquireBeforeNpcTalk': 'None', 'AcquireItemList': [],
           'CompleteType': 'EQuestCompleteType::Auto', 'CompleteValue': 'None', 'CompleteMapId': {'Value': 1000},
           'CompleteTeleportName': 'None', 'CompleteNpcTalk': 'None', 'bEnableGiveup': True,
           'NextQuestName': 'None', 'RepeatType': 'EQuestRepeatType::None'}
    row.update(extra)
    return row


def goal(kind, *values, map_id=1000):
    return {'Type': 'EQuestObjectiveType::' + kind, 'Value': list(values) + [''] * (5 - len(values)),
            'MapId': {'Value': map_id}, 'QuestMovePointName': 'None', 'bOptional': False}


NPCS = {'N_Giver': 10, 'N_Taker': 11, 'M_Wolf_01': 20, 'M_Wolf_02': 21}
ITEMS = {'Gold': 930100001, 'Potion': 530126003, 'Pelt': 600000001, 'Sword': 700000001}


class QuestConverterTests(unittest.TestCase):
    def setUp(self):
        self.quests = [
            quest_row('HQ1', 1, NextQuestName='HQ2'),
            quest_row('HQ2', 2, AcquireType='EQuestAcquireType::Npc', AcquireValue='N_Giver',
                      AcquireBeforeNpcTalk='HQ2_Accept', CompleteType='EQuestCompleteType::Npc',
                      CompleteValue='N_Taker', CompleteNpcTalk='HQ2_Complete',
                      AcquirableConditions=[{'Type': 'EQuestAcquirableCondition::ClearQuest', 'Value': 'HQ1'},
                                            {'Type': 'EQuestAcquirableCondition::SubZone', 'Value': 'Zone_A'}]),
        ]
        self.steps = [
            {'QuestName': 'HQ2', 'Order': 2, 'StartNpcTalk': 'None', 'ClearNpcTalk': 'None',
             'GoalList': [goal('KillNpc', 'M_Wolf_01, M_Wolf_02', '4'), goal('CollectItem', 'Pelt', '3', 'TRUE'),
                          goal('CollectFakeItem', 'M_Wolf_01', '9000', '2')],
             'FailConditionType': 'EQuestFailCondition::None'},
            {'QuestName': 'HQ2', 'Order': 1, 'StartNpcTalk': 'HQ2_caption', 'ClearNpcTalk': 'None',
             'GoalList': [goal('AskNpc', 'N_Taker', 'HQ2_Ask1')], 'FailConditionType': 'EQuestFailCondition::None'},
        ]
        self.rewards = [{'Group': '2', 'ExpReward': 500,
                         'ItemRewards': [{'Item': 'Gold', 'Count': 1400}],
                         'ItemNormalRewards': [{'Item': 'Potion', 'Count': 50}, {'Item': 'Unknown_b', 'Count': 1}],
                         'ItemSelectRewards': [], 'RandomReward': 'None'}]
        self.class_rewards = [{'Group': '2', 'Class': 'ECharacterClass::Gladiator', 'ItemRewards': [],
                               'ItemNormalRewards': [{'Item': 'Sword', 'Count': 1}]}]
        self.strings = [{'Name': 'STR_HQ2', 'Title': {'Key': 'T2'},
                         'ObjDescArray': [{'StepDesc': {'Key': 'S1'}, 'ObjDescList': [{'Key': 'O1'}]}]}]
        self.unlocks = [{'ID': {'Value': 44}, 'ContentUnlockType': 'EContentsUnlockType::Chat', 'UnlockLevel': 0,
                         'UnlockQuestNameLight': 'HQ1', 'UnlockQuestNameDark': 'HQ9'},
                        {'ID': {'Value': 1}, 'ContentUnlockType': 'EContentsUnlockType::BossChallenge',
                         'UnlockLevel': 45, 'UnlockQuestNameLight': 'None', 'UnlockQuestNameDark': 'None'}]
        self.data = quests.convert(self.quests, self.steps, self.rewards, self.class_rewards, self.strings,
                                   self.unlocks, NPCS, ITEMS, {}, {'T2': 'Wolves', 'S1': 'Ask', 'O1': 'Talk'})

    def test_quest_header_npcs_and_prerequisites(self):
        q = self.data['quests']['HQ2']
        self.assertEqual((q['id'], q['title'], q['race'], q['map'], q['type']), (2, 'Wolves', 'Light', 1000, 'Hero'))
        self.assertEqual((q['start_npc'], q['finish_npc'], q['start_talk'], q['finish_talk']),
                         (10, 11, 'HQ2_Accept', 'HQ2_Complete'))
        self.assertEqual(q['prerequisites'], ['HQ1'])
        self.assertEqual(q['previous'], ['HQ1'])
        self.assertEqual(q['conditions'], [{'type': 'SubZone', 'value': 'Zone_A'}])
        self.assertIsNone(self.data['quests']['HQ1']['start_npc'])

    def test_steps_are_ordered_and_objectives_resolved(self):
        steps = self.data['quests']['HQ2']['steps']
        self.assertEqual([s['order'] for s in steps], [1, 2])
        ask = steps[0]
        self.assertEqual(ask['npc'], 11)
        self.assertEqual(ask['objectives'][0], {'type': 'AskNpc', 'npc': 11, 'npc_name': 'N_Taker',
                                                'talk': 'HQ2_Ask1', 'count': 1, 'map': 1000, 'text': 'Talk'})
        self.assertEqual((ask['text'], ask['start_talk']), ('Ask', 'HQ2_caption'))
        kill, collect, fake = steps[1]['objectives']
        self.assertEqual((kill['npcs'], kill['count']), ([20, 21], 4))
        self.assertEqual((collect['item'], collect['count'], collect['flag']), (600000001, 3, 'TRUE'))
        self.assertEqual((fake['npcs'], fake['chance'], fake['count']), ([20], 9000, 2))

    def test_rewards_split_kina_items_and_class_items(self):
        r = self.data['quests']['HQ2']['rewards']
        self.assertEqual((r['exp'], r['kina']), (500, 1400))
        self.assertEqual(r['items'], [[530126003, 50], ['Unknown_b', 1]])
        self.assertEqual(r['class_items'], {'Gladiator': [[700000001, 1]]})
        self.assertEqual(self.data['quests']['HQ1']['rewards'], {'exp': 0, 'kina': 0, 'items': []})

    def test_unlocks_keep_only_quest_gated_rows(self):
        self.assertEqual(self.data['unlocks'], [{'id': 44, 'type': 'Chat', 'level': 0, 'light': 'HQ1', 'dark': 'HQ9'}])

    def test_unknown_objective_keeps_raw_values(self):
        o = quests.objective(goal('KillPC', 'LastHit,Contribute', '50', '22'), {}, {}, {})
        self.assertEqual(o['value'], ['LastHit,Contribute', '50', '22', '', ''])

    def test_item_acquire_exports_acquire_value_as_item(self):
        rows = [quest_row('UQ1', 9001, AcquireType='EQuestAcquireType::Item', AcquireValue='Scroll_A',
                          CompleteType='EQuestCompleteType::UI')]
        data = quests.convert(rows, [], npc_ids={}, item_ids={'Scroll_A': 530199001}, env_ids={}, text={})
        q = data['quests']['UQ1']
        self.assertEqual(q['acquire'], 'Item')
        self.assertEqual(q['acquire_items'], [[530199001, 1]])

    def test_mark_progression_cap_flags_high_level_quests(self):
        data = {'quests': {'HQ1': {'level': 10}, 'HQ2': {'level': 46, 'unavailable': False}}}
        quests.mark_progression_cap(data, cap=45)
        self.assertNotIn('unavailable', data['quests']['HQ1'])
        self.assertTrue(data['quests']['HQ2']['unavailable'])
        self.assertEqual(data['progression']['unavailable_quests'], 1)

    def test_spawn_check_counts_spawned_npcs(self):
        with tempfile.TemporaryDirectory() as tmp:
            spawns = Path(tmp) / 'npcs.json'
            spawns.write_text(json.dumps({'maps': {'1000': [{'npc_id': 10}, {'npc_id': 20}, {'npc_id': 21}]}}))
            report = quests.spawn_check(self.data, spawns)
        self.assertEqual((report['quests'], report['npc_start'], report['start_spawned']), (2, 1, 1))
        self.assertEqual((report['npc_finish'], report['finish_spawned']), (1, 0))
        self.assertEqual(report['missing'], ['N_Taker'])


if __name__ == '__main__':
    unittest.main()
