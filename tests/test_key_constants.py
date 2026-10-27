import unittest

from tools.key_constants import binary_key_candidates


class KeyConstantTests(unittest.TestCase):
    def dwords(self, key, base=0x44, displacement=32):
        return b"".join(b"\xc7" + bytes([base]) + b"\x24" + bytes([displacement + i * 4])
                        + key[i * 4:i * 4 + 4] for i in range(8))

    def test_stack_dword_constants_reconstruct_in_byte_order(self):
        key = bytes(range(32))
        code = self.dwords(key)
        found = list(binary_key_candidates(code, 100))
        self.assertEqual(len(found), 1)
        self.assertEqual(found[0][0], key)
        self.assertEqual(found[0][1]["offset"], 100)
        self.assertEqual(found[0][1]["instruction_bytes"], len(code))

    def test_rejects_nonconsecutive_or_incomplete_stores(self):
        code = bytearray(self.dwords(bytes(range(32))))
        self.assertEqual(list(binary_key_candidates(code[:-1])), [])
        code[8 + 3] = 99
        self.assertEqual(list(binary_key_candidates(code)), [])
        code = self.dwords(bytes(range(32)))
        self.assertEqual(list(binary_key_candidates(b"\x48" + code)), [])

    def test_qword_register_to_stack_pairs(self):
        key = bytes(range(32))
        code = b"".join(b"\x48\xb8" + key[i * 8:i * 8 + 8]
                        + b"\x48\x89\x44\x24" + bytes([32 + i * 8]) for i in range(4))
        found = list(binary_key_candidates(code))
        self.assertEqual(len(found), 1)
        self.assertEqual(found[0][0], key)
        self.assertEqual(found[0][1]["encoding"], "x64_consecutive_imm64_stores")
        self.assertEqual(list(binary_key_candidates(code.replace(b"\x48\x89", b"\x48\x88"))), [])

    def test_extended_base_and_signed_displacements(self):
        key = bytes(range(32))
        # r13-32 ... r13-4, each a C7 /0 dword immediate store.
        code = b"".join(b"\x41\xc7\x45" + bytes([224 + i * 4]) + key[i * 4:i * 4 + 4] for i in range(8))
        self.assertEqual(list(binary_key_candidates(code))[0][0], key)
