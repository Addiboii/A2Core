import json
import tempfile
import unittest
from pathlib import Path

from tools import protocol_coverage


class ProtocolCoverageTests(unittest.TestCase):
    def test_server_usage_and_status(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'World').mkdir()
            (root / 'World/A.cs').write_text('const ushort X = 0x3633;   // OBSERVED 2026-10-13\nvar y = 0x8C07; // CANDIDATE\n',
                                             encoding='utf-8')
            (root / 'World/obj').mkdir()
            (root / 'World/obj/B.cs').write_text('0x9999', encoding='utf-8')   # build output is not a source
            usage = protocol_coverage.server_usage(root)
        self.assertEqual(sorted(usage), [0x3633, 0x8c07])
        self.assertEqual(usage[0x3633]['labels']['OBSERVED'], 1)
        known = {0x3633: ('MyCharAppear_NT', 'game'), 0x8c07: ('CoinShopOpen_RS', 'game'), 0x1234: ('Never_NT', 'game')}
        table = {r['op']: r for r in protocol_coverage.rows(known, usage, {'0x3633': 5, '0x5555': 2})}
        self.assertEqual(table['0x3633']['status'], 'named, labelled OBSERVED')
        self.assertEqual(table['0x8c07']['status'], 'named, no OBSERVED label')
        self.assertEqual((table['0x5555']['status'], table['0x5555']['recorded']), ('recorded, never named by us', 2))
        self.assertNotIn('0x1234', table)   # neither named nor recorded: left out
        text = protocol_coverage.render(list(table.values()), {'recordings': 1}, len(known))
        self.assertIn('| 0x5555 | ? | 2 | 0 |  |', text)

    @unittest.skipUnless(protocol_coverage.OBSERVED.is_file(), 'the recorded counts are kept with the research notes only')
    def test_tracked_counts_are_opcode_counts_only(self):
        doc = json.loads(protocol_coverage.OBSERVED.read_text(encoding='utf-8'))
        self.assertEqual(set(doc), {'recordings', 'server_to_client'})
        self.assertTrue(all(k.startswith('0x') and isinstance(v, int) for k, v in doc['server_to_client'].items()))


if __name__ == '__main__':
    unittest.main()
