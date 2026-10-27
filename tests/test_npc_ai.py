import random
import struct
import unittest

from tools import npc_ai
from tools.combat import Target
from tools.npc_ai import (build_npc_move, build_npc_move_start, build_npc_stop, npc_ai_tick, engage,
                          AGGRO_RANGE, LEASH_RANGE)


def heading_degrees(u16):
    return u16 * 360 / 65536


class MoveBuilderTests(unittest.TestCase):
    # Official recording (research/npc-movement.md): NPC 17202 walking, NPC 47494
    # (NpcData 2101095) walk start, stop, then run start after being attacked.
    WALK_UPDATE = '1c37b286010401404cc579a575c600769e462932293201'
    WALK_START = '1b3786f30204b03663c5404bc8c6000ea2465382538201'
    STOP = '1a3786f3020000b18b84c58f90c8c6004ea2465782'
    RUN_START = '1b3786f3020503b18b84c58f90c8c6004ea24671367136'

    def check(self, sample, builder, key, offset, run=None):
        raw = bytes.fromhex(sample)
        position = struct.unpack_from('<3f', raw, offset)
        heading = heading_degrees(struct.unpack_from('<H', raw, offset + 12)[0])
        args = (key, position, heading) if run is None else (key, position, heading, run)
        self.assertEqual(builder(*args).hex(), sample)

    def test_walk_update_matches_official(self):
        self.check(self.WALK_UPDATE, build_npc_move, 17202, 6, run=False)

    def test_walk_start_matches_official(self):
        self.check(self.WALK_START, build_npc_move_start, 47494, 6, run=False)

    def test_run_start_matches_official(self):
        self.check(self.RUN_START, build_npc_move_start, 47494, 7, run=True)

    def test_stop_matches_official(self):
        self.check(self.STOP, build_npc_stop, 47494, 7)

    def test_heading_matches_travel_direction(self):
        # 0x705e was sent while the NPC moved by (-117, +47) per second.
        self.assertAlmostEqual(npc_ai.heading_to((0, 0), (-117, 47)), heading_degrees(0x705e), delta=0.5)


def mob(key=2000, aggressive=True, position=(0.0, 0.0, 100.0)):
    return Target(key=key, name='mob', hp=1000, hp_max=1000, position=position, npc_id=1,
                  aggressive=aggressive, walk_speed=120.0, run_speed=450.0, radius=60)


def opcodes(replies):
    return [int.from_bytes(r[:2], 'little') for r in replies]


class BehaviourTests(unittest.TestCase):
    def run_for(self, state, start, ms, step=50):
        out = []
        for now in range(start, start + ms, step):
            out += npc_ai_tick(state, now, random.Random(1))
        return out

    def test_aggressive_monster_chases_and_stops_in_reach(self):
        target = mob()
        state = {'targets': {target.key: target}, 'position': (AGGRO_RANGE - 100, 0.0, 120.0)}
        first = npc_ai_tick(state, 0, random.Random(1)) + npc_ai_tick(state, 50, random.Random(1))
        self.assertEqual(opcodes(first), [0x8d21, 0x371b])
        self.assertTrue(first[1][5:7] == b'\x05\x03' or first[1][4:6] == b'\x05\x03')
        out = self.run_for(state, 100, 4000)
        self.assertIn(0x371a, opcodes(out))
        gap = abs(state['position'][0] - target.position[0])
        self.assertLessEqual(gap, target.radius + npc_ai.REACH + 1)
        self.assertEqual(target.position[2], 120.0)

    def test_passive_monster_ignores_player_until_hit(self):
        target = mob(aggressive=False)
        state = {'targets': {target.key: target}, 'position': (300.0, 0.0, 100.0)}
        self.assertNotIn(0x371b, opcodes(npc_ai_tick(state, 0, random.Random(1))))
        self.assertEqual(opcodes(engage(state, target, 10)), [0x8d21])
        self.assertIn(0x371b, opcodes(npc_ai_tick(state, 60, random.Random(1))))

    def test_leash_returns_home_and_restores_hp(self):
        target = mob()
        state = {'targets': {target.key: target}, 'position': (500.0, 0.0, 100.0)}
        self.run_for(state, 0, 500)
        target.hp = 400
        state['position'] = (LEASH_RANGE + 500, 0.0, 100.0)
        out = self.run_for(state, 500, 30000)
        # Home again (it may have started an idle wander around the spawn since).
        self.assertLessEqual(max(map(abs, target.position[:2])), npc_ai.WANDER_RADIUS + 1)
        self.assertIn(0x371a, opcodes(out))
        self.assertEqual(target.hp, target.hp_max)
        self.assertEqual(opcodes(out).count(0x8d21), 1)   # combat ends once

    def test_dead_monster_returns_to_spawn_for_respawn(self):
        target = mob()
        state = {'targets': {target.key: target}, 'position': (500.0, 0.0, 100.0)}
        self.run_for(state, 0, 800)
        self.assertNotEqual(target.position[:2], (0.0, 0.0))
        target.hp = 0
        self.assertEqual(npc_ai_tick(state, 900), [])
        self.assertEqual(target.position, (0.0, 0.0, 100.0))

    def test_idle_monster_wanders_near_player_only(self):
        far, near = mob(key=1, aggressive=False), mob(key=2, aggressive=False, position=(20000.0, 0.0, 0.0))
        state = {'targets': {1: far, 2: near}, 'position': (5000.0, 0.0, 100.0)}
        out = self.run_for(state, 0, 40000, step=100)
        moved = {int.from_bytes(r[2:3], 'little') for r in out if r[:2] == b'\x1b\x37'}
        self.assertEqual(moved, {1})
        self.assertLessEqual(max(abs(far.position[0]), abs(far.position[1])), npc_ai.WANDER_RADIUS + 1)

    def test_merchants_and_static_npcs_do_not_move(self):
        merchant = mob()
        merchant.relationship, merchant.function = 3, 'BuySell'
        totem = mob(key=2001)
        totem.walk_speed = totem.run_speed = 0.0
        state = {'targets': {2000: merchant, 2001: totem}, 'position': (100.0, 0.0, 100.0)}
        self.assertEqual(self.run_for(state, 0, 20000, step=200), [])

    def test_citizens_stroll_but_never_chase(self):
        citizen = mob()
        citizen.relationship = 3
        state = {'targets': {2000: citizen}, 'position': (100.0, 0.0, 100.0)}
        out = self.run_for(state, 0, 40000, step=100)
        self.assertIn(0x371b, opcodes(out))
        self.assertNotIn(0x8d21, opcodes(out))
        self.assertTrue(all(r[5:7] != b'\x05\x03' for r in out))   # walks only
        self.assertLessEqual(max(map(abs, citizen.position[:2])), npc_ai.CITIZEN_WANDER_RADIUS + 1)


if __name__ == '__main__':
    unittest.main()
