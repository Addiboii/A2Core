import hashlib
import os
import unittest

from tools.archive_keys import AES256, key_candidates, validate
from tools.probe_archive_keys import CandidateCheck


class ArchiveKeyTests(unittest.TestCase):
    def test_hex_candidates_preserve_offsets_and_reject_longer_hex_strings(self):
        value = "0123456789abcdef" * 4
        found = list(key_candidates(b"0x" + value.encode() + b"\0", 100))
        self.assertEqual(found, [(bytes.fromhex(value), {"offset": 102, "encoding": "ascii"})])
        self.assertEqual(list(key_candidates((value + "f").encode())), [])
        wide = list(key_candidates((value + "\0").encode("utf-16le")))
        self.assertEqual(wide[0][0], bytes.fromhex(value))
        self.assertEqual(wide[0][1]["encoding"], "utf-16le")

    def test_hash_validation_requires_all_targets_and_reports_padding_length(self):
        plain = b"synthetic index"  # 15 bytes, no dependency on real game data.
        targets = [{"path": str(i), "ciphertext": b"x" * 16,
                    "index_sha1": hashlib.sha1(plain).hexdigest()} for i in range(2)]
        matches = validate(bytes(32), targets, lambda k, c: plain + b"\0")
        self.assertEqual([m["hash_matched_bytes"] for m in matches], [15, 15])
        targets[1]["index_sha1"] = "00" * 20
        self.assertEqual(validate(bytes(32), targets, lambda k, c: plain + b"\0"), [])
        self.assertEqual(validate(bytes(32), targets[:1], lambda k, c: plain + b"\0"), [])

    def test_repeated_candidates_are_tested_once_and_wrong_key_not_retained(self):
        called = []
        targets = [{"path": "x", "ciphertext": b"x" * 16, "index_sha1": "00" * 20}]
        def decrypt(key, cipher):
            called.append(key)
            return bytes(16)
        check = CandidateCheck(targets, decrypt)
        check.observe(b"a" * 64 + b"\0", 0)
        check.observe(b"a" * 64 + b"\0", 0)
        self.assertEqual(len(called), 1)
        self.assertEqual(check.verified, [])

    @unittest.skipUnless(os.name == "nt", "Windows CNG")
    def test_aes256_ecb_against_nist_known_answer(self):
        key = bytes.fromhex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4")
        encrypted = bytes.fromhex("f3eed1bdb5d2a03c064b5a7e3db181f8")
        with AES256() as aes:
            self.assertEqual(aes.decrypt(key, encrypted).hex(), "6bc1bee22e409f96e93d7e117393172a")
            with self.assertRaises(ValueError):
                aes.decrypt(bytes(16), encrypted)
            with self.assertRaises(ValueError):
                aes.decrypt(key, encrypted[:-1])

    @unittest.skipUnless(os.name == "nt", "Windows CNG")
    def test_candidate_accepted_only_after_two_real_aes_hash_checks(self):
        key = bytes.fromhex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4")
        pairs = [
            ("f3eed1bdb5d2a03c064b5a7e3db181f8", "6bc1bee22e409f96e93d7e117393172a"),
            ("591ccb10d410ed26dc5ba74a31362870", "ae2d8a571e03ac9c9eb76fac45af8e51"),
        ]
        targets = [{"path": str(i), "ciphertext": bytes.fromhex(c),
                    "index_sha1": hashlib.sha1(bytes.fromhex(p)).hexdigest()}
                   for i, (c, p) in enumerate(pairs)]
        with AES256() as aes:
            check = CandidateCheck(targets, aes.decrypt)
            check.observe(key.hex().encode() + b"\0", 400)
            self.assertEqual(len(check.verified), 1)
            self.assertEqual(len(check.verified[0]["validated_indexes"]), 2)
            self.assertEqual(check.verified[0]["source"]["offset"], 400)
            self.assertEqual(validate(bytes(32), targets, aes.decrypt), [])
