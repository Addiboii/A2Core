import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from inspect_clear_assets import classify


class ClearAssetTests(unittest.TestCase):
    def test_only_exact_observed_empty_pattern_is_classified(self):
        identifier = bytes.fromhex("0102030405060708")
        chunk = b"nCoI\x03\0\0\0" + identifier + bytes(28)
        self.assertEqual(classify(chunk, identifier.hex()), "empty_container_header_candidate")
        for changed in (chunk[:-1], chunk + b"\0", chunk[:-1] + b"\x01",
                        b"BAD!" + chunk[4:], chunk[:4] + b"\x04" + chunk[5:]):
            self.assertEqual(classify(changed, identifier.hex()), "unclassified")
        self.assertEqual(classify(chunk, "00" * 8), "unclassified")
