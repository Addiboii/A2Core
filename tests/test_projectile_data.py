import json
import unittest

from tools import projectile_data


class ProjectileBuilderTests(unittest.TestCase):
    def test_convert(self):
        rows = [{'ID': {'Value': 10002001}, 'ProjectileType': 'EProjectileType::Collide', 'Speed': 4000, 'ArrivalFixTimeMin': 0,
                 'ArrivalFixTimeMax': 0, 'CollideMoveValue01': 2500},
                {'ID': {'Value': 0}, 'ProjectileType': 'EProjectileType::Target', 'Speed': 1}]
        self.assertEqual(projectile_data.convert(rows), {10002001: ['Collide', 4000, 0, 0, 2500]})

    def test_tracked_file_is_from_build_26262663(self):
        text = projectile_data.OUT.read_text(encoding='utf-8')
        doc = json.loads(text)
        self.assertNotIn('\n ', text)
        self.assertEqual(doc['build'], '26262663')
        self.assertEqual(len(doc['projectiles']), 2468)   # every SkillProjectile.dat row
        # Rows the recordings' flights were checked against (see the builder's docstring).
        self.assertEqual(doc['projectiles']['170800001'][:4], ['Splash_FixTime_Target', 1200, 550, 550])
        self.assertEqual(doc['projectiles']['143400001'][:2], ['Target', 5000])


if __name__ == '__main__':
    unittest.main()
