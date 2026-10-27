import json
from pathlib import Path
import unittest

from tools.starter_equipment import build_equipped_inventory, starter_weapon
from tools.starter_skills import starter_profile
from tools.game_server import game_followup_replies


class StarterEquipmentTests(unittest.TestCase):
    def test_gladiator_begin_weapon_by_race(self):
        self.assertEqual(starter_weapon(starter_profile(1, 1)), 110160001)
        self.assertEqual(starter_weapon(starter_profile(9, 2)), 110160008)
        self.assertIsNone(starter_weapon(None))

    def test_requested_inventory_contains_equipped_local_weapon(self):
        state = {"character": {"pc_creation_id": 1, "gender": 1}}
        replies = game_followup_replies(b"\x10\x56", state=state)
        self.assertEqual(replies, [build_equipped_inventory(110160001)])
        self.assertEqual(state["equipped_weapon"], 110160001)
        self.assertEqual(len(replies[0]), 106)

    def test_record_matches_official_equipped_sword(self):
        fixture = Path(__file__).resolve().parents[1] / "research/local/starter-official-bodies.json"
        if not fixture.exists():
            self.skipTest("local official extraction unavailable")
        bodies = [bytes.fromhex(x) for x in json.loads(fixture.read_text())]
        packet = next(b for b in bodies if b[:2] == b"\x11\x56")
        # Override only the test identity to compare the entire observed record.
        key = int.from_bytes(packet[77:85], "little")
        built = build_equipped_inventory(150260004, item_level=5, item_key=key)
        self.assertEqual(built[5:97], packet[76:168])
        self.assertEqual(built[-9:], packet[-9:])
