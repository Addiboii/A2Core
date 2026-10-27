import struct
import unittest

from tools.inspect_encryption_refs import ImageCapture, analyze, followup_snapshot, STARTUP_NEEDLES, call_references


class EncryptionReferenceTests(unittest.TestCase):
    def test_call_references_and_anchor_gate(self):
        image = ImageCapture()
        image.observe(bytes(1024), 0)
        image.observe(b'\xe8'+struct.pack('<i',100-605),600)
        image.observe(b'\x48\x8d\x0d'+struct.pack('<i',100-707),700)
        result=call_references(image,[100])
        self.assertEqual(result['candidates'][0]['instruction_rva_candidate'],600)
        self.assertEqual(result['candidates'][0]['target_rva'],100)
        self.assertEqual([r['reference_kind'] for r in result['candidates']], ['CALL','LEA'])
        self.assertFalse(result['limit_reached'])
        plan={'anchors':[{'rva':0,'hex':'ff'}],
              'targets':[{'label':'test','rva':100,'size':7}], 'call_targets':[100]}
        self.assertNotIn('direct_calls',followup_snapshot(image,plan))
        with self.assertRaises(ValueError):call_references(image,list(range(9)))

    def test_startup_profile_is_separate_and_finds_wide_routing_reference(self):
        data = bytearray(1024)
        needle = 'DefaultLoginServer'.encode('utf-16le')
        data[100:100+len(needle)] = needle
        data[200:213] = b'EncryptionKey'
        data[600:607] = b'\x48\x8d\x0d' + struct.pack('<i', 100-607)
        image = ImageCapture()
        image.observe(data, 0)
        result = analyze(image, STARTUP_NEEDLES)
        self.assertEqual([m['needle'] for m in result['string_matches']], ['DefaultLoginServer'])
        self.assertEqual(result['reference_candidates'][0]['target_rva'], 100)
        self.assertEqual(result['exported_code_bytes'], 263)
        self.assertEqual([m['needle'] for m in analyze(image)['string_matches']], ['EncryptionKey'])

    def test_followup_requires_matching_anchors_and_checks_unread_spans(self):
        image = ImageCapture()
        image.observe(b"code\0AES key\0extra", 100)
        plan = {"anchors": [{"rva": 100, "hex": b"code".hex()}], "targets": [
            {"label": "diagnostic", "rva": 105, "size": 13, "encoding": "ascii"},
            {"label": "unread", "rva": 500, "size": 10}]}
        result = followup_snapshot(image, plan)
        self.assertEqual(result["status"], "anchors_matched")
        self.assertEqual(result["windows"][0]["diagnostic_text"], "AES key")
        self.assertEqual(result["windows"][0]["hex"], b"AES key\0".hex())
        self.assertEqual(result["windows"][1]["status"], "not_fully_readable")
        plan["anchors"][0]["hex"] = b"nope".hex()
        self.assertEqual(followup_snapshot(image, plan)["windows"], [])

    def test_followup_rejects_oversized_windows(self):
        image = ImageCapture()
        image.observe(b"code", 0)
        plan = {"anchors": [{"rva": 0, "hex": b"code".hex()}], "targets": [
            {"label": "too large", "rva": 0, "size": 4097}]}
        with self.assertRaises(ValueError):
            followup_snapshot(image, plan)

    def test_signed_rip_reference_resolves_to_string_and_exports_bounded_context(self):
        data = bytearray(1024)
        needle = b"EncryptionKey"
        data[100:100 + len(needle)] = needle
        data[600:607] = b"\x48\x8d\x0d" + struct.pack("<i", 100 - 607)
        image = ImageCapture()
        image.observe(data, 0)
        result = analyze(image)
        self.assertEqual(len(result["reference_candidates"]), 1)
        ref = result["reference_candidates"][0]
        self.assertEqual(ref["target_rva"], 100)
        self.assertEqual(ref["instruction_rva_candidate"], 600)
        self.assertEqual(result["exported_code_bytes"], 263)
        self.assertEqual(result["string_matches"][ref["string_indexes"][0]]["needle"], "EncryptionKey")

    def test_pointer_table_indirect_references_resolve(self):
        data = bytearray(4096)
        needle = b"region_list"
        data[0x100:0x100 + len(needle)] = needle
        struct.pack_into("<Q", data, 0x200, 0x100)
        struct.pack_into("<i", data, 0x603, 0x200 - (0x600 + 7))
        data[0x600] = 0x48
        data[0x601] = 0x8d
        data[0x602] = 0x0d
        image = ImageCapture()
        image.observe(data, 0)
        result = analyze(image, ["region_list"])
        self.assertEqual([m["needle"] for m in result["string_matches"]], ["region_list"])
        self.assertEqual(result["reference_candidates"], [])
        self.assertEqual(len(result["indirect_reference_candidates"]), 1)
        ref = result["indirect_reference_candidates"][0]
        self.assertEqual(ref["instruction_rva_candidate"], 0x600)
        self.assertEqual(ref["pointer_entry_rva"], 0x200)
        self.assertEqual(ref["indirect_targets"], [0x100])

    def test_absolute_pointer_uses_image_base(self):
        data = bytearray(4096)
        needle = b"global_eu_region_list"
        data[0x100:0x100 + len(needle)] = needle
        struct.pack_into("<Q", data, 0x200, 0x140001000 + 0x100)
        struct.pack_into("<i", data, 0x603, 0x200 - (0x600 + 7))
        data[0x600] = 0x48
        data[0x601] = 0x8d
        data[0x602] = 0x0d
        image = ImageCapture()
        image.observe(data, 0)
        result = analyze(image, ["global_eu_region_list"], image_base=0x140001000)
        self.assertEqual(list(result["pointer_table_entries"].keys()), ["0x200"])
        refs = result["indirect_reference_candidates"]
        self.assertEqual(len(refs), 1)
        self.assertEqual(refs[0]["indirect_targets"], [0x100])

    def test_unread_gaps_are_not_exported_as_real_code(self):
        image = ImageCapture()
        image.observe(b"\0EncryptionKey\0", 100)
        image.observe(b"\x48\x8d\x0d" + struct.pack("<i", 101 - 607), 600)
        result = analyze(image)
        self.assertEqual(result["exported_code_bytes"], 7)
        self.assertFalse(image.readable(114, 600))
        self.assertTrue(image.readable(600, 607))

    def test_overlapping_capture_and_wide_string_start(self):
        image = ImageCapture()
        image.observe(bytes(100), 0)
        image.observe(bytes(10) + "prefix AES key\0".encode("utf-16le"), 90)
        result = analyze(image)
        match = next(m for m in result["string_matches"] if m["needle"] == "AES key")
        self.assertEqual(match["string_start_rva_candidate"], 100)
        self.assertEqual(image.ranges, [(0, 130)])
