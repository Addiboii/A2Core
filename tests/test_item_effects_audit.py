import subprocess
import sys
import unittest
from pathlib import Path


class ItemEffectsAuditTests(unittest.TestCase):
    def test_audit_reports_no_blocking_gaps(self):
        root = Path(__file__).resolve().parents[1]
        proc = subprocess.run(
            [sys.executable, str(root / 'tools/audit_item_effects.py')],
            capture_output=True,
            text=True,
            check=True,
            cwd=root,
        )
        self.assertIn('"gaps": []', proc.stdout)


if __name__ == '__main__':
    unittest.main()
