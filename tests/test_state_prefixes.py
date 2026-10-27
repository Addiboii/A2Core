import hashlib
import struct
import unittest

from tools.aion_protocol import state_message_fields, compressed_messages


class StatePrefixTests(unittest.TestCase):
    def test_two_times_preserve_delta_and_leave_remaining_state_unparsed(self):
        body = struct.pack("<HHQQ", 0x3615, 0, 1700000000000, 1699974800000) + bytes(10)
        fields = state_message_fields(body)
        self.assertEqual(fields["secondary_minus_primary_ms"], -7 * 60 * 60 * 1000)
        self.assertEqual(fields["unparsed_body_bytes"], 10)
        self.assertNotIn("tail", fields)
        with self.assertRaises(ValueError):
            state_message_fields(body[:19])

    def test_game_login_tail_reports_hashed_candidate_fields(self):
        tail = bytearray(824)
        tail[0] = 1
        tail[1:5] = (0x2A14).to_bytes(4, "little")
        tail[5] = 5
        tail[6:11] = b"$Hero"
        tail[57:65] = (1791458470537).to_bytes(8, "little")
        struct.pack_into("<3f", tail, 65, -100.5, 54664.25, 24089.0)
        tail[81:89] = (1791458319470).to_bytes(8, "little")
        tail[372:376] = (1204).to_bytes(4, "little")
        tail[767] = 5
        tail[768:773] = b"acct1"
        tail[811] = 4
        tail[812:816] = b"1204"
        tail[816] = 4
        tail[817:821] = b"5201"
        body = struct.pack("<HHQQ", 0x3615, 0, 1791465861099, 1791440661099) + tail
        tail_fields = state_message_fields(body)["tail"]
        self.assertEqual(tail_fields["record_count_candidate"], 1)
        self.assertEqual(tail_fields["class_template_u32_candidate"], 0x2A14)
        self.assertEqual(tail_fields["character_name_candidate"], "$Hero")
        self.assertEqual(tail_fields["tail_time_ms_candidate_a"], 1791458470537)
        self.assertEqual(tail_fields["tail_time_ms_candidate_b"], 1791458319470)
        self.assertEqual(tail_fields["position_xyz_candidate"], [-100.5, 54664.25, 24089.0])
        self.assertEqual(tail_fields["world_id_candidate"], 1204)
        self.assertEqual(tail_fields["account_string_sha256"],
                         hashlib.sha256(b"acct1").hexdigest())
        self.assertEqual(tail_fields["digit_id_candidate_1"], "1204")
        self.assertEqual(tail_fields["digit_id_candidate_2"], "5201")

    def test_entity_shape_hashes_reference_and_decodes_five_floats(self):
        reference = b"abcdefgh"
        for size in (57, 61):
            body = b"\x34\x36" + reference + b"\0" + struct.pack("<5f", -125.5, 20, 350, 0, 90) + bytes(size - 31)
            fields = state_message_fields(body)
            self.assertEqual(fields["entity_reference_sha256"], hashlib.sha256(reference).hexdigest())
            self.assertEqual(fields["position_xyz_candidate"], [-125.5, 20, 350])
            self.assertEqual(fields["orientation_pair_candidate"], [0, 90])
            self.assertEqual(fields["unparsed_body_bytes"], size - 31)
            # Literal-only LZ4 encoding of one complete inner frame.
            inner = bytes([len(body) + 4]) + body
            envelope = b"\xff\xff" + struct.pack("<I", len(inner)) + b"\xf0" + bytes([len(inner) - 15]) + inner
            self.assertEqual(compressed_messages(envelope)["inner_frames"][0]["decoded_fields"], fields)

    def test_entity_shape_rejects_nonfinite_fields_and_unobserved_layouts(self):
        body = b"\x34\x36" + bytes(9) + struct.pack("<5f", 1, 2, 3, 0, 90) + bytes(26)
        invalid_flag = bytearray(body)
        invalid_flag[10] = 1
        nonfinite = bytearray(body)
        struct.pack_into("<f", nonfinite, 11, float("nan"))
        for invalid in (body[:-1], body + b"x", invalid_flag, nonfinite):
            with self.assertRaises(ValueError):
                state_message_fields(invalid)


if __name__ == "__main__":
    unittest.main()
