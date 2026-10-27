from datetime import datetime, timedelta, timezone
import unittest

from tools.aion_protocol import Frame
from tools.analyze_movement import analyze_frames, body_structure


def utc(seconds):
    return (datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=seconds)).isoformat()


def frame(seconds, size):
    return {"available_in_capture_utc": utc(seconds), "wire_length": size}


def cues():
    return [{"action": action, "cue_utc": utc(seconds)} for seconds, action in
            ((0, "stand_still"), (20, "walk_forward"), (23, "stop"), (43, "walk_backward"), (46, "stop"))]


class MovementAnalysisTests(unittest.TestCase):
    def test_delayed_bursts_and_short_followups_are_correlated_without_equating_cue_and_action(self):
        frames = [frame(5, 11), frame(22, 31), frame(22, 31), frame(22.1, 31), frame(22.3, 29),
                  frame(44, 31), frame(44.1, 31), frame(44.2, 29)]
        result = analyze_frames(frames, [], cues())
        self.assertEqual([b["movement_cue_candidate"] for b in result["bursts"]], ["walk_forward", "walk_backward"])
        self.assertEqual([b["update_frames"] for b in result["bursts"]], [3, 2])
        self.assertAlmostEqual(result["bursts"][0]["cue_to_first_update_ms"], 2000)
        self.assertEqual(result["bursts"][0]["same_capture_timestamp_intervals"], 1)
        self.assertAlmostEqual(result["bursts"][0]["median_positive_update_interval_ms"], 100, places=2)
        self.assertEqual(result["bursts"][1]["following_29_byte_frame_utc"], utc(44.2))

    def test_distant_or_prior_burst_and_late_short_message_are_not_assigned(self):
        result = analyze_frames([frame(18, 31), frame(33, 31), frame(35, 29)], [], cues())
        self.assertEqual(len(result["bursts"]), 2)
        self.assertTrue(all(b["movement_cue_candidate"] is None for b in result["bursts"]))
        self.assertTrue(all(b["following_29_byte_frame_utc"] is None for b in result["bursts"]))

    def test_cue_windows_are_half_open_and_require_chronology(self):
        result = analyze_frames([frame(20, 31), frame(23, 29)], [], cues())
        self.assertEqual(result["cue_windows"][0]["client_wire_length_counts"], {})
        self.assertEqual(result["cue_windows"][1]["client_wire_length_counts"], {31: 1})
        self.assertEqual(result["cue_windows"][2]["client_wire_length_counts"], {29: 1})
        with self.assertRaises(ValueError):
            analyze_frames([], [], list(reversed(cues())))

    def test_body_statistics_are_counts_not_payload_or_opcode_claims(self):
        result = body_structure([Frame(0, 34, 1, bytes(30)), Frame(31, 34, 1, bytes([1]) * 30)])
        self.assertEqual(result["samples"], 2)
        self.assertEqual(result["distinct_two_byte_headers"], 2)
        self.assertEqual(result["per_byte_distinct_values"], [2] * 30)
        self.assertEqual(result["consistent_float_triplet_offsets"], [])
        self.assertNotIn("body", result)


if __name__ == "__main__":
    unittest.main()
