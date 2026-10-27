import struct
import unittest

from tools import class_skills
from tools.game_server import game_followup_replies
from tools.starter_casts import cast_control_replies
from tools.starter_equipment import starter_weapon
from tools.starter_skills import build_skill_list, starter_profile


def request(skill):
    return (b"\x00\x38\x01\x01" + struct.pack("<i", skill) + b"\x02\x01"
            + struct.pack("<4f", 0, 1, 2, 3) + b"\x02" + struct.pack("<q", 1000))


def state_for(class_name, level, race=1):
    return {'learned_skills': {s for s, _ in class_skills.learned_skills(class_name, level)},
            'equipped_weapon': class_skills.starter_weapon(class_name, race), 'resource_tick_ms': 0,
            'resources': {'hp': 1500, 'hp_max': 1500, 'mp': 1000, 'mp_max': 1000, 'sp': 100000, 'sp_max': 100000}}


def accepted(replies):
    return len(replies) == 3 and struct.unpack('<H', replies[0][3:5])[0] == 0


class ClassSkillTests(unittest.TestCase):
    def test_level_one_matches_recovered_starter_kits(self):
        for key, profile in class_skills.json.loads(
                (class_skills.ROOT / 'tools/data/starter-skills.json').read_text()).items():
            with self.subTest(key):
                self.assertEqual([s for s, _ in class_skills.learned_skills(profile['class'], 1)], sorted(profile['skills']))

    def test_kit_grows_with_level_and_is_complete_at_cap(self):
        for name in ('Gladiator', 'Templar', 'Assassin', 'Cleric', 'Ranger', 'Elementalist', 'Sorcerer', 'Chanter'):
            with self.subTest(name):
                counts = [len(class_skills.learned_skills(name, level)) for level in (1, 10, 25, 100)]
                self.assertEqual(counts, sorted(counts))
                self.assertLess(counts[0], counts[2])
                unlocks = class_skills.data()['classes'][name]['unlocks'] + class_skills.data()['common_unlocks']
                self.assertEqual(counts[3], len({skill for skill, _, _ in unlocks}))
                self.assertLessEqual(counts[3], 127)   # one 0x5100 skill-list packet

    def test_unlock_boundary(self):
        # Gladiator Mocking Blade (11290000) unlocks at level 3.
        self.assertNotIn(11290000, dict(class_skills.learned_skills('Gladiator', 2)))
        self.assertIn(11290000, dict(class_skills.learned_skills('Gladiator', 3)))

    def test_every_class_has_its_weapon(self):
        expected = {'Gladiator': 'Greatsword', 'Templar': 'Sword', 'Assassin': 'Dagger', 'Cleric': 'Mace',
                    'Ranger': 'Bow', 'Elementalist': 'Orb', 'Sorcerer': 'Magicbook', 'Chanter': 'Staff'}
        for name, weapon in expected.items():
            for race in (1, 2):
                item = class_skills.starter_weapon(name, race)
                self.assertEqual(class_skills.weapon_type(item), weapon)
        self.assertEqual(starter_weapon(starter_profile(2, 1)), 110260001)   # Light Templar sword
        self.assertEqual(starter_weapon(starter_profile(10, 2)), 110260008)  # Dark Templar sword

    def test_levels_clamp_to_recovered_cap(self):
        self.assertEqual((class_skills.clamp_level(0), class_skills.clamp_level(500)), (1, class_skills.data()['max_level']))

    def test_skill_list_carries_skill_levels(self):
        body = build_skill_list([(11020000, 3), 11010000])
        self.assertEqual(body[:3], b"\x00\x51\x02")
        self.assertEqual(body[4:10], struct.pack('<iBB', 11020000, 3, 3))
        self.assertEqual(body[20:26], struct.pack('<iBB', 11010000, 1, 1))
        with self.assertRaises(ValueError):
            build_skill_list([(11020000, 1), (11020000, 2)])


class GeneralCastTests(unittest.TestCase):
    def test_any_learned_class_skill_casts_with_its_costs_and_cooldown(self):
        state = state_for('Gladiator', 25)
        definition = class_skills.skill(11290000)   # Mocking Blade: 200 MP, 20 s
        self.assertTrue(accepted(cast_control_replies(request(11290000), state, 1000)))
        self.assertEqual(state['resources']['mp'], 1000 - definition['cost']['mp'])
        self.assertFalse(accepted(cast_control_replies(request(11290000), state, 1000 + definition['cooldown_ms'] - 1)))
        self.assertTrue(accepted(cast_control_replies(request(11290000), state, 1000 + definition['cooldown_ms'])))

    def test_skill_above_level_is_not_learned(self):
        self.assertFalse(accepted(cast_control_replies(request(11290000), state_for('Gladiator', 2), 1000)))

    def test_weapon_requirement_uses_equipped_weapon_type(self):
        state = state_for('Gladiator', 25)
        state['equipped_weapon'] = class_skills.starter_weapon('Templar', 1)   # a sword
        self.assertFalse(accepted(cast_control_replies(request(11020000), state, 1000)))
        self.assertTrue(accepted(cast_control_replies(request(11000100), state, 1000)))   # dodge needs no weapon

    def test_other_classes_cast_their_own_skills(self):
        for name in ('Templar', 'Cleric', 'Ranger', 'Sorcerer'):
            with self.subTest(name):
                state = state_for(name, 25)
                castable = [s for s in sorted(state['learned_skills'])
                            if class_skills.skill(s)['type'] == 'Active'
                            and class_skills.skill(s)['cost']['mp'] <= 1000 and class_skills.skill(s)['cost']['hp'] == 0]
                self.assertTrue(castable)
                self.assertTrue(accepted(cast_control_replies(request(castable[0]), state, 1000)))


class NativeLevelTests(unittest.TestCase):
    def load_complete(self, level, creation=2):
        state = {'level': level, 'character': {'pc_creation_id': creation, 'gender': 1, 'name': 'Tester'}}
        body = b"\x22\x36" + (7).to_bytes(4, "little", signed=True) + b"\x00" + b"\x00" * 20
        return state, game_followup_replies(body, now_ms=1791465861099, state=state)

    def test_spawn_sends_the_level_kit_and_class_weapon(self):
        state, replies = self.load_complete(40)
        kit = class_skills.learned_skills('Templar', 40)
        self.assertEqual(state['learned_skills'], {s for s, _ in kit})
        self.assertIn(build_skill_list(kit), replies)
        self.assertEqual(state['equipped_weapon'], 110260001)
        _, low = self.load_complete(1)
        self.assertIn(build_skill_list(class_skills.learned_skills('Templar', 1)), low)

    def test_default_level_stays_one(self):
        state = {'character': {'pc_creation_id': 1, 'gender': 1}}
        body = b"\x22\x36" + (7).to_bytes(4, "little", signed=True) + b"\x00" + b"\x00" * 20
        game_followup_replies(body, now_ms=1791465861099, state=state)
        self.assertEqual(state['learned_skills'], set(starter_profile(1, 1)['skills']))


if __name__ == '__main__':
    unittest.main()
