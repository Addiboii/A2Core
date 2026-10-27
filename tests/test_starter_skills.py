import json
from pathlib import Path
import struct
import unittest

from tools.starter_skills import starter_profile, build_skill_list, build_skill_slots
from tools.game_server import game_followup_replies


class StarterSkillTests(unittest.TestCase):
    def test_creation_id_is_not_world_pc_id(self):
        male = starter_profile(1, 1)
        female = starter_profile(1, 2)
        self.assertEqual((male["pc_id"], female["pc_id"]), (5, 6))
        self.assertEqual(male["class"], "Gladiator")
        self.assertEqual(male["resource"], "MP")
        self.assertIsNone(starter_profile(99999, 1))

    def test_only_free_level_one_starter_skills_on_hotbar(self):
        p = starter_profile(1, 1)
        self.assertEqual(p["slots"], [[0, 11020000], [1, 11010000], [2, 11190000]])
        self.assertIn(11000100, p["skills"])
        self.assertNotIn(11290000, p["skills"])
        self.assertTrue(all(skill in p["skills"] for _, skill in p["slots"]))

    def test_initialization_uses_selected_class_and_sends_skills_before_slots(self):
        state = {"character": {"name": "Hero", "gender": 2, "pc_creation_id": 1}}
        replies = game_followup_replies(b"\x22\x36" + struct.pack("<i", 1) + bytes(21), state=state)
        p = starter_profile(1, 2)
        # Hotbar, then the content-unlock list, then the dummy and map NPC spawns.
        spawns = [r for r in replies if r[:2] == b"\x41\x36"]
        self.assertEqual(replies[-len(spawns) - 3:-len(spawns) - 1],
                         [build_skill_list(p["skills"]), build_skill_slots(p["slots"])])
        self.assertEqual(replies[-len(spawns) - 1][:2], b"\x57\xe2")
        self.assertEqual(state["learned_skills"], set(p["skills"]))
        # key(1), mask(4), flag(1), string(1+4), server(2), then PcData ID.
        self.assertEqual(struct.unpack_from("<i", replies[1], 15)[0], 6)

    def test_official_skill_and_hotbar_layouts(self):
        fixture = Path(__file__).resolve().parents[1] / "research/local/starter-official-bodies.json"
        if not fixture.exists():
            self.skipTest("local official capture extraction unavailable")
        bodies = [bytes.fromhex(x) for x in json.loads(fixture.read_text())]
        slots = next(b for b in bodies if b[:2] == b"\x00\x56")
        self.assertEqual(build_skill_slots([(0, 9865), (1, 9868)]), slots)
        skills = next(b for b in bodies if b[:2] == b"\x00\x51")
        offset = 3
        for _ in range(skills[2]):
            flag = skills[offset]
            self.assertIn(flag, (1, 5))
            skill = struct.unpack_from("<i", skills, offset + 1)[0]
            # The sample has a single flag-5 entry with autoload count 3.
            # Ready entries must match every byte, not just an ID anchor.
            if flag == 1:
                self.assertEqual(build_skill_list([skill])[3:], skills[offset:offset + 16])
            offset += 16 + (1 if flag == 5 else 0)
        self.assertEqual(offset, len(skills))


if __name__ == "__main__":
    unittest.main()
