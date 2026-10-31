import json
import unittest

from tools import abnormal_stack_rules


def row(aid, group=None, count=1, overlap='None'):
    return {'ID': {'Value': aid}, 'GroupId': aid if group is None else group, 'AbnormalOverlapCount': count,
            'AbnormalOverlapTimeType': f'EAbnormalOverlapTimeType::{overlap}', 'AbnormalReplaceType': 'EAbnormalReplaceType::Replace'}


class AbnormalStackRuleTests(unittest.TestCase):
    def test_only_rows_that_are_not_the_plain_case(self):
        rules = abnormal_stack_rules.rules([row(3015), row(103, count=3, overlap='Separate'), row(151, group=150), row(104, overlap='Longer')])
        self.assertEqual(rules, {'103': [103, 3, 'Separate'], '104': [104, 1, 'Longer'], '151': [150, 1, 'None']})

    def test_tracked_file_is_compact_and_from_the_table(self):
        text = abnormal_stack_rules.OUT.read_text(encoding='utf-8')
        doc = json.loads(text)
        self.assertNotIn('\n ', text)
        self.assertEqual(doc['fields'], ['group', 'overlap_count', 'overlap_time'])
        self.assertNotIn('3015', doc['abnormals'])   # the dodge's abnormal is the plain case (0x382b refresh, OBSERVED)
        self.assertTrue(all(v[2] in ('None', 'Renew', 'Longer', 'Separate') for v in doc['abnormals'].values()))


if __name__ == '__main__':
    unittest.main()
