import random
import struct
import unittest

from tools import class_skills, combat
from tools.aion_protocol import encode_varint
from tools.starter_casts import cast_control_replies, parse_starter_cast, respawn_targets, RESPAWN_MS


class FixedRandom(random.Random):
    """Deterministic rolls: always the low end, never critical (or always, if asked)."""
    def __init__(self, critical=False):
        super().__init__(0)
        self.critical = critical

    def randint(self, a, b):
        return a

    def random(self):
        return 0.0 if self.critical else 0.999


GREATSWORD = 110160001   # level-one greatsword: 4-6 damage, Critical 150


class DamageTests(unittest.TestCase):
    def test_keen_strike_follows_the_candidate_formula(self):
        # Keen Strike: flat 51 + 43.00% of (weapon 4 + FixingDamage 6 at level 1).
        hits = combat.skill_hits(11020000, 'Gladiator', 1, GREATSWORD, rng=FixedRandom())
        self.assertEqual(hits, [{'amount': round(51 + 10 * 4300 / 10000), 'critical': False, 'effect_id': 1102000011}])

    def test_level_raises_damage_through_fixing_damage(self):
        low = combat.skill_hits(11020000, 'Gladiator', 1, GREATSWORD, rng=FixedRandom())[0]['amount']
        high = combat.skill_hits(11020000, 'Gladiator', 100, GREATSWORD, rng=FixedRandom())[0]['amount']
        fixing = combat.base_stats('Gladiator', 100)['FixingDamage']
        self.assertEqual(high, round(51 + (4 + fixing) * 0.43))
        self.assertGreater(high, low)

    def test_critical_and_defense(self):
        normal = combat.skill_hits(11020000, 'Gladiator', 1, GREATSWORD, rng=FixedRandom())[0]['amount']
        critical = combat.skill_hits(11020000, 'Gladiator', 1, GREATSWORD, rng=FixedRandom(critical=True))[0]
        self.assertTrue(critical['critical'])
        self.assertEqual(critical['amount'], round((51 + 10 * 0.43) * combat.CRITICAL_MULTIPLIER))
        defended = combat.skill_hits(11020000, 'Gladiator', 1, GREATSWORD, target_defense=1000, rng=FixedRandom())[0]['amount']
        self.assertEqual(defended, round(normal / 2))

    def test_multi_row_skill_hits_per_row_and_pvp_values(self):
        rows = class_skills.skill(11010000)['damage']   # Rending Blow: two Damage rows
        self.assertEqual(len(combat.skill_hits(11010000, 'Gladiator', 1, GREATSWORD, rng=FixedRandom())), len(rows))
        pvp = combat.skill_hits(11020000, 'Gladiator', 1, GREATSWORD, rng=FixedRandom(), pvp=True)[0]['amount']
        self.assertEqual(pvp, round(48 + 10 * 3890 / 10000))

    def test_non_damage_skills_and_unknown_skills_deal_nothing(self):
        self.assertEqual(combat.skill_hits(11000100, 'Gladiator', 1, GREATSWORD), [])   # dodge
        self.assertEqual(combat.skill_hits(999, 'Gladiator', 1, GREATSWORD), [])

    def test_hits_reduce_hp_to_zero(self):
        target = combat.Target(key=2, name='Training dummy', hp=60, hp_max=60)
        dealt = combat.apply_hits(target, [{'amount': 40}, {'amount': 40}])
        self.assertEqual((dealt, target.hp, target.dead), (60, 0, True))


def cast(skill, target_key=1, location=(1, 2, 3)):
    return (b"\x00\x38\x01\x01" + struct.pack("<i", skill) + b"\x02" + encode_varint(target_key)
            + struct.pack("<4f", 0, *location) + b"\x02" + struct.pack("<q", 1000))


def gladiator(level=25, dummy_hp=1000, dummy_at=(301.0, 2.0, 3.0)):
    return {'learned_skills': {s for s, _ in class_skills.learned_skills('Gladiator', level)},
            'class': 'Gladiator', 'level': level, 'equipped_weapon': GREATSWORD, 'rng': FixedRandom(),
            'resources': {'hp': 1500, 'hp_max': 1500, 'mp': 5000, 'mp_max': 5000, 'sp': 100000, 'sp_max': 100000},
            'targets': {combat.TRAINING_DUMMY_KEY: combat.Target(combat.TRAINING_DUMMY_KEY, 'Training Dummy',
                                                                 dummy_hp, dummy_hp, position=dummy_at)}}


class WireTests(unittest.TestCase):
    def test_hit_and_death_match_public_fixtures(self):
        # a2kit parse_test.go TestParseHit / death samples (commit fb693ba0), opcode prefix added.
        hit = bytes.fromhex("c77c0400f5a302e026a80000024bed9f4101000000904e240100")
        self.assertEqual(combat.build_skill_hit(15943, 37365, 11020000, 1101000011, 36)[2:], hit)
        self.assertEqual(combat.build_death(37365)[2:], bytes.fromhex("f5a3020003"))
        critical = combat.build_skill_hit(15943, 37365, 11020000, 1101000011, 36, critical=True)
        self.assertEqual(critical[2:][12], 3)

    def test_npc_appear_and_hp_match_official_recording(self):
        # research/official-combat-capture.md (2026-10-13): two ordinary monsters, one
        # aggressive (NpcData Aggro_A), and an HP update after a 25-damage hit.
        plain = combat.Target(39829, '', 665, 665, position=(-1842.2103271484375, -32727.173828125, 19021.0),
                              npc_id=2101095, level=7, direction=147.33645629882812)
        self.assertEqual(combat.build_npc_appear(plain, spawn_tag=0xc4ed40f5).hex(),
                         "413695b702042000670f20000002bb46e6c459aeffc6009a944622561343c6680199059905640000"
                         "00640000000000000000000000000000000000000064000000640000000100000000000000000000"
                         "0000000000000000000600010007000000f540edc400")
        aggressive = combat.Target(23403, '', 931, 931, position=(1126.2197265625, -37312.74609375, 19731.0),
                                   npc_id=2101072, level=9, direction=142.03125)
        self.assertEqual(combat.build_npc_appear(aggressive, aggressive=True, spawn_tag=0xa2274d37).hex(),
                         "4136ebb6010c2000500f2000000208c78c44bfc011c700269a4600080e43006501a307a307640000"
                         "00640000000000000000000000000000000000000064000000640000000100000000000000000000"
                         "000000000000000000060001010009000000374d27a200")
        self.assertEqual(combat.build_npc_hp(47494, 640).hex(), "008d86f3020201008002000000000000")

    def test_cast_request_carries_target_key(self):
        self.assertEqual(parse_starter_cast(cast(11020000, combat.TRAINING_DUMMY_KEY))['target_key'], 1000)
        self.assertEqual(parse_starter_cast(cast(11020000))['target_key'], 1)


class DummyTests(unittest.TestCase):
    def test_targeted_skill_damages_the_dummy(self):
        state = gladiator()
        replies = cast_control_replies(cast(11020000, combat.TRAINING_DUMMY_KEY), state, 1000)
        expected = combat.skill_hits(11020000, 'Gladiator', 25, GREATSWORD, rng=FixedRandom())
        hits = [r for r in replies if r[:2] == b'\x04\x38']
        self.assertEqual(len(hits), len(expected))
        dummy = state['targets'][combat.TRAINING_DUMMY_KEY]
        self.assertEqual(dummy.hp, 1000 - sum(h['amount'] for h in expected))
        self.assertEqual(replies[-1], combat.build_npc_hp(1000, dummy.hp))
        self.assertEqual(state['combat_log'][-1]['target'], 1000)

    def test_area_skill_hits_only_within_range(self):
        near, far = gladiator(), gladiator(dummy_at=(1000.0, 2.0, 3.0))
        self.assertTrue(any(r[:2] == b'\x04\x38' for r in cast_control_replies(cast(11020000), near, 1000)))
        self.assertFalse(any(r[:2] == b'\x04\x38' for r in cast_control_replies(cast(11020000), far, 1000)))

    def test_kill_sends_zero_hp_then_despawns_and_respawns(self):
        state = gladiator(dummy_hp=1)
        replies = cast_control_replies(cast(11020000, combat.TRAINING_DUMMY_KEY), state, 1000)
        self.assertEqual(replies[-1], combat.build_npc_hp(combat.TRAINING_DUMMY_KEY, 0))
        dummy = state['targets'][combat.TRAINING_DUMMY_KEY]
        self.assertTrue(dummy.dead)
        early = cast_control_replies(cast(11020000, combat.TRAINING_DUMMY_KEY), state, 1000 + RESPAWN_MS - 1)
        self.assertFalse(any(r[:2] == b'\x04\x38' for r in early))
        self.assertEqual(respawn_targets(state, 1000 + RESPAWN_MS - 1), [])
        respawn = respawn_targets(state, 1000 + RESPAWN_MS)
        self.assertEqual(respawn, [combat.build_death(1000), combat.build_npc_appear(dummy)])
        self.assertFalse(dummy.dead)
        self.assertEqual(respawn_targets(state, 1000 + RESPAWN_MS * 2), [])
        cast_control_replies(cast(11020000, combat.TRAINING_DUMMY_KEY), state, 1000 + RESPAWN_MS)
        self.assertEqual(state['combat_log'][-1]['target'], 1000)

    def test_training_dummy_is_the_client_sandbag(self):
        dummy = combat.training_dummy((100.0, 200.0, 300.0))
        body = combat.build_npc_appear(dummy)
        self.assertEqual((dummy.npc_id, dummy.position), (2400032, (400.0, 200.0, 300.0)))
        self.assertEqual(body[:5], b'\x41\x36' + encode_varint(1000) + b'\x04')
        self.assertIn(encode_varint(5_000) * 2, body)

    def test_citizens_are_never_hit(self):
        state = gladiator()
        citizen = combat.Target(2000, 'Villager', 500, 500, position=(301.0, 2.0, 3.0), relationship=3)
        state['targets'] = {2000: citizen}
        for target_key in (1, 2000):   # area cast next to it, and aimed at it
            replies = cast_control_replies(cast(11020000, target_key), state, 1000 + target_key * 10000)
            self.assertFalse(any(r[:2] == b'\x04\x38' for r in replies))
        self.assertEqual(citizen.hp, 500)

    def test_non_damage_skill_and_rejected_cast_do_not_hit(self):
        state = gladiator()
        self.assertFalse(any(r[:2] == b'\x04\x38' for r in cast_control_replies(cast(11000100, 1000), state, 1000)))
        state['equipped_weapon'] = None   # weapon requirement fails, so no hit
        self.assertFalse(any(r[:2] == b'\x04\x38' for r in cast_control_replies(cast(11020000, 1000), state, 2000)))


if __name__ == '__main__':
    unittest.main()
