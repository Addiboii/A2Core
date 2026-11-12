import itertools
import json
import unittest
from pathlib import Path

from tools import specialized_skills

DATA = json.loads((Path(__file__).resolve().parents[1] / 'tools/data/specialized-skills.json').read_text(encoding='utf-8'))
STIGMAS = json.loads((Path(__file__).resolve().parents[1] / 'tools/data/stigmas.json').read_text(encoding='utf-8'))


class SpecializedSkillTests(unittest.TestCase):
    def test_child_ids_are_the_parts_digits_ascending(self):
        # The recorded casts (community recordings 2026-11-04 to 06): parts 30+40 of 11020000 -> 11020340, 50+30 of
        # 11010000 -> 11010350, 10+40 of 11100000 -> 11100140; one part -> 11360030.
        self.assertEqual(specialized_skills.child_id(11020000, [11020030, 11020040]), 11020340)
        self.assertEqual(specialized_skills.child_id(11010000, [11010050, 11010030]), 11010350)
        self.assertEqual(specialized_skills.child_id(11100000, [11100010, 11100040]), 11100140)
        self.assertEqual(specialized_skills.child_id(11360000, [11360030]), 11360030)
        self.assertEqual(specialized_skills.child_id(11020000, [11020050, 11020010, 11020030]), 11021350)

    def test_every_child_of_the_table_is_a_parts_combination(self):
        for parent, kids in DATA['children'].items():
            parts = [p for p, _ in DATA['parts'][parent]]
            made = {specialized_skills.child_id(int(parent), c) for n in (1, 2, 3) for c in itertools.combinations(parts, n)}
            self.assertEqual(set(kids), made, parent)
            self.assertTrue(all(str(k) in DATA['skills'] for k in kids), parent)

    def test_slots_and_part_levels(self):
        self.assertEqual(DATA['slot_levels'], [8, 12, 20])
        self.assertEqual(DATA['stigma_slots'], 5)
        self.assertEqual(DATA['parts']['11020000'], [[11020010, 8], [11020020, 8], [11020030, 8], [11020040, 12], [11020050, 16]])

    def test_chain_steps_children_follow_the_childs_chain(self):
        # Recorded in 0x5100 #643 with 11020000 parts 30+40: 11030340 and 11040340 (Skill.dat links them to 11020340).
        self.assertIn(11030340, DATA['chain_children']['11030000'])
        self.assertIn(11040340, DATA['chain_children']['11040000'])
        self.assertEqual(DATA['skills']['11030340']['chain_prev'], 11020340)
        self.assertEqual(DATA['skills']['11020340']['chain_next'], [11030340])
        self.assertEqual(DATA['skills']['11030340']['parent'], 11030000)

    def test_stigmas_are_not_mastery_parents(self):
        # A stigma's parts (InitEquipSlotType Slot_1..4) are the variants of stigmas.json, not written here.
        self.assertFalse(set(DATA['parts']) & set(STIGMAS['variants']))


if __name__ == '__main__':
    unittest.main()
