import hashlib
import json
import os
import struct
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools.archive_keys import validate, validate_utoc
from tools.deep_memory_scan import observe_deep
from tools.key_constants import faes_register_candidates, raw_key_prefilter, vector_key_candidates
from tools.probe_archive_keys import CandidateCheck, write_validated_key


class DeepMemoryKeyTests(unittest.TestCase):
    def test_raw_prefilter_rejects_low_entropy_and_long_zero_runs(self):
        self.assertFalse(raw_key_prefilter(b"\x00" * 32))
        self.assertFalse(raw_key_prefilter(bytes([1, 1, 1, 1, 2, 2, 2, 2] * 4)))
        key = bytes(range(32))
        self.assertTrue(raw_key_prefilter(key))

    def test_faes_layout_yields_key_after_guid_prefix(self):
        blob = b"\x01" * 16 + bytes(range(32)) + b"\x00" * 8
        found = list(faes_register_candidates(blob, 1000))
        self.assertEqual(len(found), 1)
        self.assertEqual(found[0][0], bytes(range(32)))
        self.assertEqual(found[0][1]["offset"], 1000 + 16)

    def test_vector_pair_reads_two_rip_relative_constants(self):
        first = bytes(range(16))
        second = bytes(range(16, 32))
        rip = 0x100
        first_end = rip + 7
        second_end = rip + 15
        pool = second_end
        disp1 = struct.pack("<i", pool - first_end)
        disp2 = struct.pack("<i", (pool + 16) - second_end)
        code = b"\x0f\x10\x05" + disp1 + b"\x66\x0f\x6f\x05" + disp2
        image = bytearray(b"\x90" * rip) + code
        image.extend(first + second + b"\x00" * 16)
        found = list(vector_key_candidates(bytes(image), 0))
        self.assertEqual(len(found), 1)
        self.assertEqual(found[0][0], bytes(range(32)))

    def test_observe_deep_stops_after_verification(self):
        key = bytes(range(32))
        targets = [{"path": "a", "ciphertext": b"x" * 16, "index_sha1": hashlib.sha1(b"ok").hexdigest()},
                   {"path": "b", "ciphertext": b"y" * 16, "index_sha1": hashlib.sha1(b"ok").hexdigest()}]

        def decrypt(_key, _cipher):
            return b"ok"

        check = CandidateCheck(targets, decrypt, deep=True, max_candidates=1000)
        blob = b"\xab" * 48 + key + b"\xff" * 64
        self.assertTrue(observe_deep(check, blob, 0x5000, "private"))
        self.assertEqual(len(check.verified), 1)
        self.assertIn(check.verified[0]["source"]["encoding"],
                      ("raw_memory_window", "faes_guid_prefix_key"))

    def test_validate_utoc_accepts_parsed_directory(self):
        chunk_ids = [b"\x01" * 12]
        mount = "/Game/"
        name = "Item.uasset"
        def fstring(value):
            encoded = value.encode()
            return struct.pack("<i", len(encoded) + 1) + encoded + b"\x00"

        mount_bytes = fstring(mount)
        name_bytes = fstring(name)
        directories = struct.pack("<IIII", 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0)
        files = struct.pack("<III", 0, 0xFFFFFFFF, 0)
        body = (mount_bytes + struct.pack("<i", 1) + directories + struct.pack("<i", 1) + files
                + struct.pack("<i", 1) + name_bytes)
        targets = [{"path": "x.utoc", "ciphertext": body, "chunk_ids": chunk_ids}]
        matches = validate_utoc(bytes(32), targets, lambda _k, c: c)
        self.assertEqual(len(matches), 1)
        self.assertEqual(matches[0]["kind"], "utoc_directory_index")

    def test_validate_requires_utoc_when_targets_supplied(self):
        plain = b"synthetic index"
        pak = [{"path": "a", "ciphertext": b"x" * 16, "index_sha1": hashlib.sha1(plain).hexdigest()},
               {"path": "b", "ciphertext": b"y" * 16, "index_sha1": hashlib.sha1(plain).hexdigest()}]
        utoc = [{"path": "bad.utoc", "ciphertext": b"\xff" * 32, "chunk_ids": [b"\x00" * 12]}]
        decrypt = lambda _k, c: plain if len(c) == 16 else c
        self.assertEqual(len(validate(bytes(32), pak, decrypt, utoc_targets=utoc)), 0)
        self.assertEqual(len(validate(bytes(32), pak, decrypt)), 2)

    def test_write_validated_key_creates_local_json(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            validated = root / "archive-key-validated.json"
            report = {"verified_keys": [{"key_hex": "aa" * 32}]}
            with patch("tools.probe_archive_keys.VALIDATED_KEY_PATH", validated):
                path = write_validated_key(report)
                self.assertEqual(path, validated)
                payload = json.loads(validated.read_text(encoding="utf-8"))
                self.assertEqual(payload, {"key_hex": "aa" * 32})
                with self.assertRaises(ValueError):
                    write_validated_key({"verified_keys": [{"key_hex": "bb" * 32}]})


if __name__ == "__main__":
    unittest.main()
