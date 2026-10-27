import unittest
import struct

from tools.aion_protocol import Incomplete, client_hello, server_hello, handoff_response, split_frames, summarize_stream, varuint, world_list, short_server_message


def vu(value):
    result = bytearray()
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    return bytes(result + bytes([value]))


def tlv(tag, value):
    size = len(value)
    length = bytes([size]) if size < 128 else b"\x82" + size.to_bytes(2, "big")
    return bytes([tag]) + length + value


def row(identifier, group, name, status=0, load=0, mask=None):
    encoded = name.encode("utf-8")
    return (identifier.to_bytes(2, "little") + bytes([group]) + identifier.to_bytes(2, "little")
            + b"\0\0" + bytes([len(encoded)]) + encoded
            + (bytes([mask]) if mask is not None else b"")
            + bytes([status, load]) + b"\x50\x01\x00")


class AionProtocolTests(unittest.TestCase):
    def test_long_stream_remains_bounded(self):
        frames, consumed = split_frames(b"\x06AB" * 5571)
        self.assertEqual((len(frames), consumed), (5571, 16713))
        with self.assertRaisesRegex(ValueError, "Frame count limit"):
            split_frames(b"\x06AB" * 16385)

    def test_game_hello_has_distinct_tags_and_extra_server_field(self):
        modulus = b"\0\x80" + bytes(254) + b"\x01"
        key = tlv(0x30, tlv(2, modulus) + tlv(2, b"\x03"))
        client = client_hello(b"\x10\x36" + vu(len(key)) + key + bytes(11))
        self.assertEqual(client["tag_candidate"], "0x3610")
        self.assertEqual(client["unparsed_trailer_bytes"], 11)
        body = struct.pack("<4H", 0x3611, 0, 60000, 0) + vu(256) + bytes(256) + bytes(12)
        server = server_hello(body)
        self.assertEqual(server["opaque_blob_bytes"], 256)
        self.assertEqual(server["additional_unknown_u16"], 0)
        self.assertEqual(server["unparsed_trailer_bytes"], 12)

    def test_handoff_destination_and_exact_body_bounds(self):
        address = b"198.51.100.25"
        body = struct.pack("<3H", 0x390F, 0, 7) + vu(len(address)) + address + struct.pack("<H", 13328)
        decoded = handoff_response(body)
        self.assertEqual(decoded["destination_address"], "198.51.100.25")
        self.assertEqual(decoded["destination_port"], 13328)
        for invalid in (body[:-1], body + b"\0", body[:-2] + b"\0\0", body.replace(b"198", b"999")):
            with self.assertRaises(ValueError):
                handoff_response(invalid)

    def test_short_reply_fields_and_exact_lengths(self):
        ping = struct.pack("<HHQQ", 0x3903, 0, 123456789, 1700000000123)
        selection = struct.pack("<HHBH", 0x390D, 42, 0, 2301)
        self.assertEqual(short_server_message(ping)["unix_time_ms_candidate"], 1700000000123)
        self.assertEqual(short_server_message(ping)["counter_candidate"], 123456789)
        self.assertEqual(short_server_message(b"\x03\x36" + ping[2:])["counter_candidate"], 123456789)
        clock = struct.pack("<HQ", 0x3600, 1700000000123)
        self.assertEqual(short_server_message(clock)["unix_time_ms_candidate"], 1700000000123)
        self.assertEqual(short_server_message(selection)["world_id_candidate"], 2301)
        self.assertEqual(short_server_message(selection)["result_field"], 42)
        for invalid in (ping[:-1], ping + b"\0", selection[:-1], selection + b"\0", b"\0\0"):
            with self.assertRaises(ValueError):
                short_server_message(invalid)
        report = summarize_stream(vu(len(ping) + 4) + ping + vu(len(selection) + 4) + selection, "server_to_client")
        self.assertEqual(report["frames"][1]["decoded_fields"]["world_id_candidate"], 2301)

    def test_length_field_represents_logical_four_byte_header(self):
        # 06 encodes logical length 6: two body bytes plus a logical uint32 length.
        # 99 02 encodes 281: 277 body bytes, transmitted with a two-byte prefix.
        data = b"\x06AB\x99\x02" + bytes(277)
        frames, consumed = split_frames(data)
        self.assertEqual(consumed, len(data))
        self.assertEqual([frame.wire_length for frame in frames], [3, 279])
        self.assertEqual([frame.logical_length for frame in frames], [6, 281])

    def test_incomplete_prefix_and_body_remain_unconsumed(self):
        for remainder in (b"\x99", b"\x99\x02\x00"):
            frames, consumed = split_frames(b"\x06AB" + remainder)
            self.assertEqual(len(frames), 1)
            self.assertEqual(consumed, 3)

    def test_noncanonical_and_overflow_varints_are_rejected(self):
        for data in (b"\x86\x00", b"\xff\xff\xff\xff\x10", b"\x80" * 6):
            with self.assertRaises(ValueError):
                varuint(data)
        with self.assertRaises(Incomplete):
            varuint(b"\x80")

    def test_too_small_and_oversized_frames_are_rejected(self):
        for data in (b"\x05", vu(16 * 1024 * 1024 + 1)):
            with self.assertRaises(ValueError):
                split_frames(data)

    def test_directory_decodes_post_patch_row_layout(self):
        body = (b"\x09\x39\0\0\x02"
                + row(1301, 1, "Synthetic_Light", status=4, load=5, mask=0x01)
                + row(2307, 2, "Synthetic_Dark_007", status=0x59, load=4) + b"\x01\x01")
        parsed = world_list(body)
        self.assertEqual(parsed["count"], 2)
        self.assertEqual(parsed["worlds"][0]["status"], 4)
        self.assertEqual(parsed["worlds"][0]["load"], 5)
        self.assertEqual(parsed["worlds"][0]["group_mask_bits"], 1)
        self.assertEqual(parsed["worlds"][1]["name"], "Synthetic_Dark_007")
        self.assertEqual(parsed["worlds"][1]["status"], 0x59)
        self.assertEqual(parsed["worlds"][1]["load"], 4)
        self.assertEqual(parsed["trailer_hex"], "0101")

    def test_directory_rejects_mismatched_repeated_ids(self):
        item = bytearray(row(1301, 1, "Synthetic"))
        item[3:5] = b"\0\0"
        with self.assertRaisesRegex(ValueError, "repeated ID"):
            world_list(b"\x09\x39\0\0\x01" + item + b"\x01\x01")

    def test_directory_rejects_unrecognized_suffix(self):
        item = bytearray(row(1301, 1, "Synthetic"))
        item[-3:] = b"\x00\x00\x00"
        with self.assertRaisesRegex(ValueError, "suffix constant"):
            world_list(b"\x09\x39\0\0\x01" + item + b"\x01\x01")

    def test_rsa_summary_exposes_parameters_without_modulus(self):
        # Synthetic public value, not copied from the captured client key.
        modulus = b"\0\x80" + bytes(254) + b"\x01"
        encoded = tlv(0x30, tlv(2, modulus) + tlv(2, b"\x03"))
        summary = client_hello(b"\0\x39" + vu(len(encoded)) + encoded + bytes(5))
        self.assertEqual(summary["modulus_bits"], 2048)
        self.assertEqual(summary["public_exponent"], 3)
        self.assertEqual(summary["unparsed_trailer_bytes"], 5)
        self.assertNotIn("modulus", summary)

    def test_summary_requires_multiple_complete_frames(self):
        self.assertIsNone(summarize_stream(b"\x06AB", "client_to_server"))
        self.assertIsNone(summarize_stream(b"\x06AB\x08X", "client_to_server"))
        self.assertEqual(summarize_stream(b"\x06AB\x06CD", "client_to_server")["frame_count"], 2)


if __name__ == "__main__":
    unittest.main()
