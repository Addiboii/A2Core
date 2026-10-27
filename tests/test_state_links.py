from io import BytesIO
from pathlib import Path
import struct
import unittest
from unittest.mock import patch

from tools.inspect_capture import inspect
from tools.inspect_state_links import shared_float_triplets, candidate_reference_links, expand_messages
from test_inspect_capture import section, interface, enhanced, ethernet_tcp


def message(tag, payload):
    return {"tag_candidate": f"0x{tag:04x}", "body": struct.pack("<H", tag) + payload,
            "available_in_capture_utc": "2026-01-01T00:00:00+00:00"}


class StateLinkTests(unittest.TestCase):
    def test_exact_triplet_at_different_offsets_and_no_raw_body_output(self):
        vector = struct.pack("<3f", -1000, 2000, 3000)
        messages = [message(1, b"\0" + vector), message(2, bytes(3) + vector), message(3, vector)]
        result = shared_float_triplets(messages)
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["float_triplet_candidate"], [-1000, 2000, 3000])
        self.assertEqual([item["body_offset"] for item in result[0]["occurrences"]], [3, 5, 2])
        self.assertTrue(all("body" not in item for item in result[0]["occurrences"]))

    def test_distinct_tags_and_exact_bytes_are_required(self):
        vector = struct.pack("<3f", 100, 200, 300)
        self.assertEqual(shared_float_triplets([message(1, vector)] * 3), [])
        changed = struct.pack("<3f", 100, 200, 300.5)
        self.assertEqual(shared_float_triplets([message(1, vector), message(2, vector), message(3, changed)]), [])
        for values in ((0, 200, 300), (float("nan"), 200, 300)):
            self.assertEqual(shared_float_triplets([message(tag, struct.pack("<3f", *values)) for tag in (1, 2, 3)]), [])
        with self.assertRaisesRegex(ValueError, "budget"):
            shared_float_triplets([message(1, vector)], body_budget=10)

    def test_reference_matches_are_hashed_and_do_not_include_source_family(self):
        reference = b"abcdefgh"
        source = message(0x3634, reference + b"\0" + struct.pack("<5f", 1, 2, 3, 0, 90) + bytes(26))
        result = candidate_reference_links([source, message(0x3633, b"x" + reference)])
        self.assertEqual(result["distinct_references"], 1)
        self.assertEqual(len(result["matches_in_other_tags"]), 1)
        self.assertEqual(result["matches_in_other_tags"][0]["body_offset"], 3)
        self.assertEqual(len(result["matches_in_other_tags"][0]["reference_sha256"]), 64)

    def test_expansion_preserves_outer_timing_and_inner_index(self):
        inner = b"\x06AB\x06CD"
        body = b"\xff\xff" + struct.pack("<I", len(inner)) + b"\x60" + inner
        wire = bytes([len(body) + 4]) + body
        metadata = [{"compressed_envelope": {}, "available_in_capture_utc": "2026-01-01T00:00:00+00:00"}]
        messages = expand_messages(wire, metadata)
        self.assertEqual([m["inner_frame_index"] for m in messages], [0, 1])
        self.assertEqual(messages[0]["available_in_capture_utc"], messages[1]["available_in_capture_utc"])

    def test_observer_receives_prefix_without_changing_serializable_report(self):
        data = section() + interface() + enhanced(ethernet_tcp(99, flags=2)) + enhanced(ethernet_tcp(100, b"\x06AB\x06CD"))
        observed = []
        with patch.object(Path, "open", return_value=BytesIO(data)):
            report = inspect(Path("fixture.pcapng"), 13700, lambda flow, direction, prefix: observed.append((direction, prefix)))
        self.assertEqual(observed, [("client_to_server", b"\x06AB\x06CD")])
        import json
        self.assertNotIn("prefix_payload", json.dumps(report))


if __name__ == "__main__":
    unittest.main()
