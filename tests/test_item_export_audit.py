import json
import subprocess
import sys
import unittest
from pathlib import Path


class ItemExportAuditTests(unittest.TestCase):
    def test_audit_invariants(self):
        root = Path(__file__).resolve().parents[1]
        proc = subprocess.run(
            [sys.executable, str(root / 'tools/audit_item_export.py')],
            capture_output=True,
            text=True,
            check=True,
            cwd=root,
        )
        report = json.loads(proc.stdout)
        inv = report['invariants']
        self.assertTrue(inv['decompose_subset_of_items_json'], inv.get('decompose_missing'))
        self.assertTrue(inv['enchant_subset_of_items_json'], inv.get('enchant_missing'))
        self.assertTrue(inv['equip_references_covered'], report.get('missing_equip_heuristic'))
        self.assertEqual(report['items_json_count'], 3555)


if __name__ == '__main__':
    unittest.main()
