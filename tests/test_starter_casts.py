import struct
import unittest
from pathlib import Path
from tools.aion_protocol import Reader, split_frames

from tools.starter_casts import (parse_starter_cast, cast_control_replies,
                                 build_skill_start, build_resource_update,
                                 regenerate_resources)


def request(skill):
    # Synthetic values in the observed 35-byte shape; raw captures stay local.
    return (b"\x00\x38\x01\x01" + struct.pack("<i", skill) + b"\x02\x01"
            + struct.pack("<4f", 0, 1, 2, 3) + b"\x02" + struct.pack("<q", 1000))


ATTACK = request(11020000)
DODGE = request(11000100)


def player_state():
    return {'learned_skills': {11000100, 11020000, 11010000},
            'equipped_weapon': 110160001, 'resource_tick_ms': 1000,
            'resources': {'hp': 1500, 'hp_max': 1500, 'mp': 200, 'mp_max': 200,
                          'sp': 100000, 'sp_max': 100000}}


class StarterCastTests(unittest.TestCase):
    def test_basic_swing_is_free_and_dodge_spends_stamina(self):
        state = player_state()
        cast_control_replies(ATTACK, state, 1000)
        self.assertTrue(state['last_cast']['accepted'])
        self.assertEqual((state['resources']['mp'], state['resources']['sp']),
                         (200, 100000))
        cast_control_replies(DODGE, state, 1001)
        self.assertTrue(state['last_cast']['accepted'])
        self.assertEqual((state['resources']['mp'], state['resources']['sp']),
                         (200, 70000))

    def test_start_matches_full_public_fixture(self):
        fixture = Path(__file__).resolve().parents[1] / 'research/local/public-handshake-review-20261009/a2kit/game/testdata/cast.bin'
        if not fixture.exists():
            self.skipTest('pinned public research fixture unavailable')
        frames, _ = split_frames(fixture.read_bytes())
        checked = 0
        for frame in frames:
            r = Reader(frame.body)
            if r.uint(2) != 0x3802:
                continue
            actor = r.varuint()
            self.assertEqual(r.uint(1), 0)
            skill, action = r.uint(4), r.uint(1)
            mask = r.uint(1)
            if mask != 2:
                continue
            target = r.varuint()
            yaw, *position = struct.unpack('<4f', r.take(16))
            speed = r.varuint()
            self.assertEqual(r.uint(1), 1)
            cooldown = r.varuint()
            self.assertEqual(r.offset, len(frame.body))
            self.assertEqual(build_skill_start(skill, action, yaw, position,
                                              target, actor, speed, cooldown), frame.body)
            checked += 1
        self.assertEqual(checked, 4)

    def test_mana_cost_rejection_and_regeneration(self):
        state = player_state()
        mana_skill = request(11010000)
        cast_control_replies(mana_skill, state, 1000)
        self.assertTrue(state['last_cast']['accepted'])
        self.assertEqual(state['resources']['mp'], 80)
        action = state['action_uid']
        replies = cast_control_replies(mana_skill, state, 1001)
        self.assertFalse(state['last_cast']['accepted'])
        self.assertEqual(len(replies), 1)
        self.assertEqual(state['action_uid'], action)
        self.assertEqual(state['resources']['mp'], 80)
        self.assertEqual(regenerate_resources(state, 1999), [])
        self.assertEqual(len(regenerate_resources(state, 3000)), 1)
        self.assertEqual(state['resources']['mp'], 130)
        cast_control_replies(mana_skill, state, 3000)
        self.assertTrue(state['last_cast']['accepted'])
        self.assertEqual(state['resources']['mp'], 10)
        regenerate_resources(state, 999999)
        self.assertEqual(state['resources']['mp'], 200)

    def test_resource_update_receiver_layout(self):
        resources = player_state()['resources']
        resources.update(mp=80, sp=70000)
        r = Reader(build_resource_update(resources))
        self.assertEqual((r.uint(2), r.varuint()), (0x364c, 1))
        self.assertEqual((r.varuint(), r.varuint()), (1500, 1500))
        pools = struct.unpack('<12i', r.take(48))
        self.assertEqual(pools[:2], (80, 200))
        self.assertEqual(pools[6:8], (70000, 100000))
        self.assertEqual(r.varuint(), 0)
        self.assertEqual(r.offset, len(r.data))

    def test_observed_requests(self):
        self.assertEqual(parse_starter_cast(ATTACK)["skill_id"], 11020000)
        self.assertEqual(parse_starter_cast(DODGE)["skill_id"], 11000100)
        self.assertIsNone(parse_starter_cast(ATTACK[:-1]))
        self.assertIsNone(parse_starter_cast(ATTACK + b"\x00"))
        bad = bytearray(ATTACK)
        struct.pack_into("<f", bad, 10, float("nan"))
        self.assertIsNone(parse_starter_cast(bad))

    def test_control_replies_and_server_cooldown(self):
        state = player_state()
        replies = cast_control_replies(DODGE, state, 1000)
        self.assertTrue(state["last_cast"]["accepted"])
        self.assertEqual(replies[0][:5], b"\x01\x38\x03\x00\x00")
        self.assertEqual(struct.unpack_from("<iBB", replies[0], 5), (11000100, 0, 0))
        self.assertEqual([r[:2] for r in replies],
                         [bytes.fromhex(x) for x in ('0138', '4c36', '0238')])
        self.assertEqual(state['resources']['sp'], 70000)
        cast_control_replies(DODGE, state, 1499)
        self.assertFalse(state["last_cast"]["accepted"])
        cast_control_replies(DODGE, state, 1500)
        self.assertTrue(state["last_cast"]["accepted"])

    def test_unowned_or_unequipped_cast_is_not_accepted(self):
        for state in ({}, {"learned_skills": {11020000}}):
            cast_control_replies(ATTACK, state, 1000)
            self.assertFalse(state["last_cast"]["accepted"])

    def test_reserved_skill_is_rejected_with_reply_instead_of_ignored(self):
        body = bytearray(request(11010000))
        body[2] = 5  # direction + reserved bool + timestamp
        body[27:27] = b'\x01'
        state = {'learned_skills': {11010000}, 'equipped_weapon': 110160001}
        replies = cast_control_replies(bytes(body), state, 1000)
        self.assertEqual(len(replies), 1)
        self.assertFalse(state['last_cast']['accepted'])
        self.assertEqual(state['last_cast']['client_action_uid'], 0)

    def test_action_uid_is_optional_field_not_mask_high_byte(self):
        body = bytearray(ATTACK)
        body[2] |= 16
        body[27:27] = b'\x07'
        self.assertEqual(parse_starter_cast(bytes(body))['client_action_uid'], 7)
