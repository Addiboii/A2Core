import tempfile
import unittest
from pathlib import Path

from tools.audit_saved_data import compare, snapshot


class SavedDataAuditTests(unittest.TestCase):
    def test_changes_and_no_contents_retained(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "changed.ini").write_text("private old content")
            (root / "removed.ini").write_text("old")
            before = snapshot(root)
            (root / "changed.ini").write_text("private new content")
            (root / "removed.ini").unlink()
            (root / "new.ini").write_text("new")
            result = compare(before, snapshot(root))
            self.assertEqual(result["changed"], ["changed.ini"])
            self.assertEqual(result["added"], ["new.ini"])
            self.assertEqual(result["removed"], ["removed.ini"])
            self.assertTrue(result["complete"])
            self.assertNotIn("private", str(before))

    def test_missing_root_and_incomplete_comparison(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "absent"
            before = snapshot(root)
            root.mkdir()
            after = snapshot(root)
            self.assertTrue(compare(before, after)["root_presence_changed"])
            after["errors"].append({"error": "denied"})
            self.assertFalse(compare(before, after)["complete"])

    def test_different_roots_rejected(self):
        with self.assertRaises(ValueError):
            compare({"root": "one"}, {"root": "two"})
