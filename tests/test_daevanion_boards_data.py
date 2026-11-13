import collections
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import daevanion_boards_data  # noqa: E402

DATA = json.loads((ROOT / 'tools/data/daevanion-boards.json').read_text(encoding='utf-8'))


def node(kind, *values, condition='None'):
    row = {'ID': {'Value': 1}, 'Board': {'Value': 11}, 'Row': 1, 'Col': 1, 'Grade': 'EItemGrade::Common',
           'Type': 'EDaevanionNodeType::' + kind, 'bAutoLearn': False, 'NeedLevel': 12,
           'NeedConditionType': 'EDaevanionNodeConditionType::' + condition, 'CostDaevanionPoint': 1, 'ResetGold': 500}
    for i in range(1, 11):
        row['Value%02d' % i] = values[i - 1] if i <= len(values) else ''
    return row


class DaevanionBuilderTests(unittest.TestCase):
    def test_node_values_by_type(self):
        self.assertEqual(daevanion_boards_data.node_values(node('Stat', 'HPMax', '50', 'Defense', '10')), [['HPMax', 50], ['Defense', 10]])
        self.assertEqual(daevanion_boards_data.node_values(node('SkillLevel', '11720000', '1')), [11720000, 1])
        self.assertEqual(daevanion_boards_data.node_values(node('Start', 'STR_X')), ['STR_X'])
        self.assertEqual(daevanion_boards_data.node_values(node('None')), [])

    def test_unexpected_rows_are_refused(self):
        with self.assertRaises(ValueError):
            daevanion_boards_data.node_values(node('Stat', 'HPMax'))
        with self.assertRaises(ValueError):
            daevanion_boards_data.node_values(node('Other'))
        with self.assertRaises(ValueError):
            daevanion_boards_data.nodes_from([node('None', condition='Quest')])

    def test_points_read_the_key_value_list(self):
        rows = [{'Level': 13, 'DaevanionPointMap': [{'Key': 'EDaevanionPointType::DaevanionCrystal', 'Value': 8}]},
                {'Level': 1, 'DaevanionPointMap': []}]
        self.assertEqual(daevanion_boards_data.points_from(rows), {'DaevanionCrystal': {'13': 8}})


class DaevanionDataTests(unittest.TestCase):
    # OBSERVED: DaevanionBoard / DaevanionNode / DaevanionMaxCapacity (research/local/tables-26262663-items) and Exp.dat
    # (research/local/exp-tables-20261016), build 26262663.
    def test_54_boards_six_per_class(self):
        self.assertEqual(len(DATA['boards']), 54)
        per_class = collections.Counter(b[0] for b in DATA['boards'].values())
        self.assertEqual(len(per_class), 9)
        self.assertTrue(all(n == 6 for n in per_class.values()))
        self.assertEqual(DATA['boards']['11'], ['Gladiator', 12, 1, 'DaevanionCrystal', 'GoldCombined'])
        self.assertEqual(DATA['boards']['81'][0], 'Chanter')
        for board_id, board in DATA['boards'].items():
            self.assertEqual(int(board_id) % 10, board[2], board_id)

    def test_12150_nodes_of_four_types(self):
        self.assertEqual(len(DATA['nodes']), 12150)
        kinds = collections.Counter(n[3] for n in DATA['nodes'].values())
        self.assertEqual(set(kinds), {'None', 'Stat', 'SkillLevel', 'Start'})
        self.assertEqual(kinds['Start'], 54)
        self.assertEqual(DATA['nodes']['110033'], [11, 3, 3, 'Stat', 'Unique', 12, 4, 500, 0, [['CombatSpeed', 150]]])
        self.assertEqual(DATA['nodes']['110037'][3:], ['SkillLevel', 'Rare', 12, 2, 500, 0, [11720000, 1]])
        self.assertEqual(DATA['nodes']['110113'][3:9], ['Start', 'None', 12, 0, 0, 1])

    def test_capacity_and_level_points(self):
        self.assertEqual(DATA['max_capacity']['DaevanionCrystal'], 840)
        self.assertEqual(list(DATA['points']), ['DaevanionCrystal'])
        crystal = DATA['points']['DaevanionCrystal']
        self.assertEqual((crystal['12'], crystal['13'], crystal['100']), (4, 8, 356))
        self.assertNotIn('11', crystal)


if __name__ == '__main__':
    unittest.main()
