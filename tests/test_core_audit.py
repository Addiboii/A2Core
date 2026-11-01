import json
import unittest
from pathlib import Path

from tools import audit_core_gameplay, class_skills


class CoreAuditTests(unittest.TestCase):
    def test_keen_strike_effective_cooldown(self):
        row = class_skills.skill(11020000)
        self.assertEqual(class_skills.effective_cooldown_ms(row), 1000)

    def test_audit_script_runs(self):
        report = audit_core_gameplay.run_audit()
        self.assertEqual(report['skills']['sample_keen_strike_effective_ms'], 1000)
        self.assertTrue(report['amplify']['export_present'])
        self.assertEqual(report['amplify']['step0_success_prob'], 6600)
        self.assertEqual(report['enchant']['unique_plus11_success'], 6500)

    def test_starter_cast_uses_effective_cooldown(self):
        from tools.starter_casts import cast_control_replies
        import struct

        def request(skill):
            return (b"\x00\x38\x01\x01" + struct.pack("<i", skill) + b"\x02\x01"
                    + struct.pack("<4f", 0, 1, 2, 3) + b"\x02" + struct.pack("<q", 1000))

        state = {'learned_skills': {11020000}, 'equipped_weapon': 110160001,
                 'resources': {'hp': 1500, 'hp_max': 1500, 'mp': 5000, 'mp_max': 5000, 'sp': 100000, 'sp_max': 100000}}
        cast_control_replies(request(11020000), state, 1000)
        self.assertTrue(state['last_cast']['accepted'])
        cast_control_replies(request(11020000), state, 1799)
        self.assertFalse(state['last_cast']['accepted'])


if __name__ == '__main__':
    unittest.main()
