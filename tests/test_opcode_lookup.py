import contextlib
import io
import unittest

from tools import opcode_lookup
from tools.packet_schema import DEFAULT_USMAP


@unittest.skipUnless(DEFAULT_USMAP.exists(), 'usmap not present (research/local/tooling/mappings)')
class OpcodeLookupTests(unittest.TestCase):
    def run_lookup(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(opcode_lookup.main(list(args)), 0)
        return out.getvalue()

    def test_names_observed_and_usmap(self):
        text = self.run_lookup('0x3602')
        self.assertIn('observed: Ping request', text)
        self.assertIn('usmap (game): DevPacketData_GameServer_Ping_RQ', text)

    def test_login_range_shows_both_families(self):
        text = self.run_lookup('3900')
        self.assertIn('usmap (login): DevPacketData_LoginServer_Handshake_RQ', text)
        self.assertIn('usmap (game):', text)
        self.assertNotIn('usmap (game):', self.run_lookup('3900', '--login'))

    def test_body_parse(self):
        text = self.run_lookup('0x3602', '--game', '--body', '0100000000000000 05000000')
        self.assertIn('"_client_latency_ms": 5', text)
        self.assertIn('parsed 12 of 12 bytes', text)

    def test_find(self):
        self.assertIn('0x3602 game  DevPacketData_GameServer_Ping_RQ', self.run_lookup('--find', 'GameServer_Ping_RQ'))


if __name__ == '__main__':
    unittest.main()
