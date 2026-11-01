"""capture_diff: field-level parity between official timelines and simulated replies."""
import json
import struct
import tempfile
import unittest
from pathlib import Path

from tools import capture_diff, game_server
from tools.packet_schema import PacketSchema
from tools.opcode_lookup import load_ledger, width_overrides


ROOT = Path(__file__).resolve().parents[1]


class CaptureDiffTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        ledger = load_ledger()
        cls.schema = PacketSchema(width_overrides=width_overrides(ledger))

    def test_identical_ping_bodies_match(self):
        now = 1760000000123
        body = game_server.build_ping_reply(0x1122334455667788, now)
        row = capture_diff.compare_bodies(self.schema, "game", body, body)
        self.assertEqual(row.status, "match")

    def test_field_mismatch_detected(self):
        now = 1760000000123
        good = game_server.build_ping_reply(0x1122334455667788, now)
        bad = bytearray(good)
        bad[2:4] = struct.pack("<H", 9)  # _result (u16 immediately after opcode)
        row = capture_diff.compare_bodies(self.schema, "game", good, bytes(bad))
        self.assertEqual(row.status, "field_mismatch")
        paths = {d.path for d in row.field_diffs}
        self.assertTrue(any("_result" in p for p in paths))

    def test_align_skips_official_map_frame(self):
        ping = game_server.build_ping_reply(1, 1000)
        map_frame = b"\x01\x36" + (999).to_bytes(8, "little")
        rows = capture_diff.align_compare(self.schema, "game", [map_frame, ping], [ping])
        self.assertEqual(rows[0].status, "match")

    def test_timeline_roundtrip(self):
        ping = game_server.build_ping_reply(42, 1000)
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "timeline-13328"
            out.mkdir()
            record = {"i": 0, "dir": "s2c", "op": "0x3603", "len": len(ping), "body": ping.hex()}
            (out / "timeline.jsonl").write_text(json.dumps(record) + "\n", encoding="utf-8")
            loaded = capture_diff.load_timeline(out)
            self.assertEqual(len(loaded), 1)
            self.assertEqual(capture_diff.record_body(loaded[0]), ping)

    def test_discover_empty_when_no_local(self):
        with tempfile.TemporaryDirectory() as tmp:
            disc = capture_diff.discover_captures(Path(tmp))
            self.assertEqual(disc["pcapng"], [])

    def test_render_markdown_no_captures(self):
        report = {"discover": {"pcapng": [], "timelines": [], "server_prefix_bins": [], "chain_runs": []}}
        text = capture_diff.render_markdown(report)
        self.assertIn("no", text.lower())
        self.assertIn("25767555", text)


if __name__ == "__main__":
    unittest.main()
