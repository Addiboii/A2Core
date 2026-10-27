from copy import deepcopy
import unittest

from tools.summarize_session import summarize, game_summary


def flow(client="192.0.2.1", start="2026-01-01T00:00:02+00:00"):
    return {"client_address": client, "server_address": "198.51.100.25", "server_port": 13328,
            "first_packet_utc": start, "last_packet_utc": start,
            "client_syn_observed": True, "directions": {}}


class SessionSummaryTests(unittest.TestCase):
    def test_entity_candidates_include_outer_and_inner_records_without_double_counting_identity(self):
        fields = {"entity_reference_sha256": "synthetic", "position_xyz_candidate": [1, 2, 3]}
        timestamp = "2026-01-01T00:00:03+00:00"
        outer = {"header_u16_le": "0x3634", "available_in_capture_utc": timestamp, "decoded_fields": fields}
        compressed = {"header_u16_le": "0xffff", "available_in_capture_utc": timestamp, "wire_length": 10,
                      "compressed_envelope": {"inner_header_u16_le_counts": {"0x3634": 1}, "uncompressed_bytes": 20,
                                              "inner_frame_count": 1, "inner_frames": [outer]}}
        sample = flow()
        sample["directions"] = {"server_to_client": {"application_framing": {"frames": [outer, compressed]}}}
        result = game_summary(sample)["entity_position_candidates"]
        self.assertEqual(result["records"], 2)
        self.assertEqual(result["distinct_reference_hashes"], 1)
        self.assertEqual(result["axis_ranges"], [[1, 1], [2, 2], [3, 3]])

    def test_handoff_match_requires_same_client_endpoint_and_later_syn(self):
        announcement = {"decoded_handoff": {"destination_address": "198.51.100.25", "destination_port": 13328},
                        "available_in_capture_utc": "2026-01-01T00:00:01+00:00"}
        login = {"flows": [{"client_address": "192.0.2.1", "directions": {"server_to_client": {
            "application_framing": {"frames": [announcement]}}}}]}
        game = {"flows": [flow(start="2026-01-01T00:00:00+00:00"), flow(client="192.0.2.2"), flow()]}
        self.assertEqual(summarize(login, game)["handoffs"][0]["announcement_to_syn_ms"], 1000)
        game["flows"].pop()
        self.assertIsNone(summarize(login, game)["handoffs"][0]["next_matching_syn_utc"])

    def test_expanded_counts_replace_envelope_and_missing_syn_has_no_relative_time(self):
        sample = flow()
        sample["directions"] = {"server_to_client": {"application_framing": {"frames": [
            {"header_u16_le": "0xffff", "available_in_capture_utc": "2026-01-01T00:00:03+00:00", "wire_length": 20,
             "compressed_envelope": {"inner_header_u16_le_counts": {"0x3615": 2}, "uncompressed_bytes": 40,
                                     "inner_frame_count": 2, "inner_frames": [{"header_u16_le": "0x3615"}] * 2}},
            {"header_u16_le": "0x3621"}]}}}
        result = game_summary(sample)
        self.assertEqual(result["outer_server_frames"], 2)
        self.assertEqual(result["expanded_server_frames"], 3)
        self.assertNotIn("0xffff", result["expanded_server_tag_counts"])
        unknown_start = deepcopy(sample)
        unknown_start["client_syn_observed"] = False
        self.assertIsNone(game_summary(unknown_start)["compressed_events"][0]["seconds_after_syn"])


if __name__ == "__main__":
    unittest.main()
