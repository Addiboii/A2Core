import random
import unittest

from tools import combat, world_npcs


class NpcCombatCalibrationTests(unittest.TestCase):
    def test_agrint_basic_hit_near_level_player(self):
        """Level-7 agrint basic (9000–11000) vs level-7 player stays in a comfortable band (CANDIDATE)."""
        skill = {'damage_min': 9000, 'damage_max': 11000}
        rng = random.Random(2)
        hits = [world_npcs.npc_skill_damage(7, skill['damage_min'], skill['damage_max'], 7, rng=rng) for _ in range(20)]
        self.assertTrue(all(30 <= h <= 45 for h in hits), hits)

    def test_level_gap_increases_damage_when_mob_is_higher(self):
        low = world_npcs.npc_skill_damage(10, 10000, 10000, 10, rng=random.Random(0))
        high = world_npcs.npc_skill_damage(10, 10000, 10000, 5, rng=random.Random(0))
        self.assertGreater(high, low)

    def test_npc_defense_scales_with_grade(self):
        normal = world_npcs.npc_defense(7, 'NormalMonster', 3, reference_pc_defense=70)
        elite = world_npcs.npc_defense(7, 'EliteMonster', 1, reference_pc_defense=70)
        self.assertEqual(normal, 70)
        self.assertGreater(elite, normal)

    def test_mitigation_uses_defense(self):
        raw = world_npcs.npc_skill_damage(7, 10000, 10000, 7, rng=random.Random(0))
        mitigated = max(1, round(raw * combat.DEFENSE_SCALE / (combat.DEFENSE_SCALE + 70)))
        self.assertLess(mitigated, raw)


if __name__ == '__main__':
    unittest.main()
