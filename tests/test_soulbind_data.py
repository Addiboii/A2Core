import json
import unittest

from tools import soulbind_data


class SoulBindBuilderTests(unittest.TestCase):
    def test_convert_shares_pools_and_skips_unbindable_items(self):
        stat = lambda name, low, high, weight: {'Key': f'EStat::{name}', 'Value': {'MinValue': low, 'MaxValue': high, 'RandomWeight': weight}}
        pool = [stat('DEX', 7, 15, 6000), stat('HPRegen', 15, 24, 4000), stat('Nothing', 1, 2, 100), stat('CON', 7, 15, 0)]
        rows = [{'ID': {'Value': 1}, 'SoulbindRandomStat': True, 'SoulbindRandomStatCount': 3, 'SubStats': pool},
                {'ID': {'Value': 2}, 'SoulbindRandomStat': True, 'SoulbindRandomStatCount': 2, 'SubStats': pool},
                {'ID': {'Value': 3}, 'SoulbindRandomStat': False, 'SoulbindRandomStatCount': 3, 'SubStats': pool},
                {'ID': {'Value': 4}, 'SoulbindRandomStat': True, 'SoulbindRandomStatCount': 0, 'SubStats': pool}]
        items, pools = soulbind_data.convert(rows, {'DEX': 2, 'HPRegen': 194, 'CON': 4})
        self.assertEqual(items, {1: [3, 0], 2: [2, 0]})
        self.assertEqual(pools, [[[2, 7, 15, 6000], [194, 15, 24, 4000]]])   # unknown stats and weight 0 left out

    def test_tracked_file_holds_the_recorded_items(self):
        text = soulbind_data.OUT.read_text(encoding='utf-8')
        doc = json.loads(text)
        self.assertNotIn('\n ', text)
        self.assertEqual(doc['build'], '26262663')
        # The bound items of the community recordings (see the builder's docstring) and their stat counts.
        self.assertEqual({i: doc['items'][i][0] for i in ('110130050', '115030052', '210540022')},
                         {'110130050': 4, '115030052': 4, '210540022': 3})
        gloves = {row[0]: row[1:3] for row in doc['pools'][doc['items']['210540022'][1]]}
        self.assertEqual((gloves[41], gloves[194], gloves[2]), ([89, 109], [15, 24], [7, 15]))   # 99/106, 16/20, 12/14 recorded
        self.assertEqual((doc['stats']['HPMax'], doc['stats']['ArmorDefense']), (193, 307))


if __name__ == '__main__':
    unittest.main()
