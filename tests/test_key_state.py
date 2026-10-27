import os
from pathlib import Path
import struct
import sys
import unittest

from tools.inspect_key_state import collect
from tools.inspect_live_build import inspect


class Checks:
    def __init__(self):
        self.verified = []
        self.offers = []

    def offer(self, value, source):
        self.offers.append((value, source))
        if value == bytes(range(32)):
            self.verified.append(source)

    def observe(self, data, offset):
        pass


class KeyStateTests(unittest.TestCase):
    def fixture(self, count=1, capacity=1):
        base = 0x100000
        manager = bytearray(192)
        struct.pack_into("<Qii", manager, 0x28, 0x200000, count, capacity)
        memory = {base + 0x100: b"code", base + 0x200: bytes(manager),
                  base + 0x400: bytes(16), 0x200000: bytes(16) + bytes(range(32)) + bytes(16)}
        reads = []
        def read(address, size):
            reads.append((address, size))
            if address not in memory or size > len(memory[address]):
                raise OSError("unavailable")
            return memory[address][:size]
        plan = {"anchors": [{"rva": 0x100, "hex": b"code".hex()}],
                "manager_rva": 0x200, "delegate_rva": 0x400}
        return base, memory, reads, read, plan

    def test_anchored_map_read_submits_binary_key_at_value_offset(self):
        base, memory, reads, read, plan = self.fixture()
        check = Checks()
        result = collect(base, 0x10000, read, plan, check)
        self.assertEqual(result["status"], "anchors_matched")
        self.assertEqual(check.verified, [{"encoding": "targeted_binary_window",
                         "region": "manager_elements_candidate", "offset": 16}])
        self.assertEqual(reads[-1], (0x200000, 64))
        self.assertNotIn(base + 0x400, [address for address, size in reads])
        self.assertNotIn("hex", str(result))

    def test_anchor_mismatch_prevents_pointer_following(self):
        base, memory, reads, read, plan = self.fixture()
        memory[base + 0x100] = b"nope"
        result = collect(base, 0x10000, read, plan, Checks())
        self.assertEqual(result["status"], "anchor_mismatch")
        self.assertEqual(reads, [(base + 0x100, 4)])

    def test_unreasonable_map_dimensions_do_not_read_element_pointer(self):
        for count, capacity in [(129, 129), (2, 1), (-1, 10), (0, 0)]:
            base, memory, reads, read, plan = self.fixture(count, capacity)
            result = collect(base, 0x10000, read, plan, Checks())
            self.assertEqual(result["map_dimensions"]["sampled_bytes"], 0)
            self.assertNotIn(0x200000, [address for address, size in reads])

    @unittest.skipUnless(os.name == "nt", "Windows process API")
    def test_targeted_reader_reads_verified_process_and_enforces_request_bounds(self):
        def finish(base, size, read):
            self.assertEqual(read(base, 2), b"MZ")
            with self.assertRaises(ValueError):
                read(base, 4097)
            with self.assertRaises(ValueError):
                read(0, 16)
            return {"checked": True}
        result = inspect(os.getpid(), Path(sys.executable), finish_observer=finish)
        self.assertEqual(result["targeted_observation"], {"checked": True})
        self.assertEqual(result["targeted_read_requests"], 1)
        self.assertEqual(result["targeted_requested_bytes"], 2)
