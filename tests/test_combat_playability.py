import json
import random
import struct
import unittest
from pathlib import Path

from tools import audit_combat, combat, npc_ai, world_npcs
from tools.aion_protocol import encode_varint
from tools.starter_casts import cast_control_replies, respawn_targets, RESPAWN_MS


GREATSWORD = 110160001


def cast_keen(target_key, now_ms=1000):
    return (b"\x00\x38\x01\x01" + struct.pack("<i", 11020000) + b"\x02" + encode_varint(target_key)
            + struct.pack("<4f", 0, 301, 2, 3) + b"\x02" + struct.pack("<q", now_ms))


class AuditCombatTests(unittest.TestCase):
    def test_audit_runs_without_gaps(self):
        report = audit_combat.run_audit()
        self.assertEqual(report['build']['level_cap'], 45)
        self.assertTrue(report['hp_curve']['monotonic'])
        self.assertGreaterEqual(report['field_mobs']['levels_with_field_hostile'], 35)
        self.assertEqual(report['combat_formula']['effective_gcd_ms'], 1000)
        self.assertEqual(report['gaps'], [])

    def test_hp_curve_matches_world_npcs_for_all_cap_levels(self):
        for level in range(1, 46):
            self.assertGreater(world_npcs.hp_for_level(level), 0)


class LevelBandPacketTests(unittest.TestCase):
    """Packet-level smoke for mob respawn and aggro (Python dev loop mirrors server rules)."""

    def test_respawn_timer_matches_starter_casts(self):
        state = {'learned_skills': {11020000}, 'class': 'Gladiator', 'level': 25,
                 'equipped_weapon': GREATSWORD, 'rng': random.Random(0),
                 'resources': {'hp': 1500, 'hp_max': 1500, 'mp': 5000, 'mp_max': 5000, 'sp': 100000, 'sp_max': 100000},
                 'targets': {combat.TRAINING_DUMMY_KEY: combat.Target(combat.TRAINING_DUMMY_KEY, 'dummy', 1, 1,
                                                                       position=(301.0, 2.0, 3.0))}}
        cast_control_replies(cast_keen(combat.TRAINING_DUMMY_KEY), state, 1000)
        self.assertEqual(respawn_targets(state, 1000 + RESPAWN_MS - 1), [])
        self.assertTrue(respawn_targets(state, 1000 + RESPAWN_MS))

    def test_aggressive_mob_enters_chase(self):
        target = combat.Target(key=2000, name='mob', hp=100, hp_max=100, position=(0, 0, 0), aggressive=True,
                               walk_speed=100, run_speed=400, radius=50)
        state = {'targets': {2000: target}, 'position': (npc_ai.AGGRO_RANGE - 100, 0.0, 0.0)}
        rng = random.Random(1)
        first = npc_ai.npc_ai_tick(state, 0, rng) + npc_ai.npc_ai_tick(state, 50, rng)
        opcodes = [int.from_bytes(r[:2], 'little') for r in first]
        self.assertIn(0x371b, opcodes)  # NpcMoveStart — chase after aggro


class LootJsonSanityTests(unittest.TestCase):
    def test_loot_json_loads(self):
        loot = json.loads(Path('tools/data/loot.json').read_text(encoding='utf-8'))
        self.assertIn('2100002', loot['npcs'])
        self.assertIn('envs', loot)


if __name__ == '__main__':
    unittest.main()
