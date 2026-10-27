import struct
import unittest

from tools.aion_protocol import compressed_messages, lz4_block, lz4_block_encode, summarize_stream


class CompressionTests(unittest.TestCase):
    def test_literal_block_and_extended_literal_length(self):
        self.assertEqual(lz4_block(b"\x50hello", 5), b"hello")
        self.assertEqual(lz4_block(b"\xf0\x05" + bytes(range(20)), 20), bytes(range(20)))

    def test_encoder_round_trips_through_decoder(self):
        samples = [
            b"",
            b"a",
            b"hello",
            b"A" * 5000,
            bytes(range(32)) * 40,
            b"abcabcabcabcabcabcabc",
            b"x" + b"abcdef" * 300 + b"abcdefabcdef",
        ]
        for data in samples:
            encoded = lz4_block_encode(data)
            self.assertEqual(lz4_block(encoded, len(data)), data,
                             "round-trip failed for sample of %d bytes" % len(data))

    def test_overlapping_match_and_extended_match_length(self):
        # Literal A, distance 1, four-byte match, final five literals.
        self.assertEqual(lz4_block(b"\x10A\x01\x00\x50hello", 10), b"AAAAAhello")
        # Low nibble 15 plus extension 2 means a 21-byte match.
        self.assertEqual(lz4_block(b"\x1fA\x01\x00\x02\x50hello", 27), b"A" * 22 + b"hello")

    def test_truncated_bad_distance_and_expansion_are_rejected(self):
        for data, expected in ((b"\xf0", 20), (b"\x50hi", 5), (b"\x10A\x00\x00", 5),
                               (b"\x10A\x02\x00", 5), (b"\x10A\x01\x00", 4),
                               (b"\x10A", 2), (b"\x50hello", 4)):
            with self.assertRaises(ValueError):
                lz4_block(data, expected)
        with self.assertRaisesRegex(ValueError, "bound"):
            lz4_block(b"", 4 * 1024 * 1024 + 1)

    def test_envelope_exposes_inner_frame_metadata_without_bodies(self):
        # Two independent three-byte wire frames: logical length 6, two-byte tag.
        inner = b"\x06\x15\x36\x06\x33\x36"
        body = b"\xff\xff" + struct.pack("<I", len(inner)) + b"\x60" + inner
        parsed = compressed_messages(body)
        self.assertEqual(parsed["inner_frame_count"], 2)
        self.assertEqual(parsed["inner_header_u16_le_counts"], {"0x3615": 1, "0x3633": 1})
        self.assertEqual(parsed["inner_frames"][1]["offset"], 3)
        self.assertNotIn("body", parsed["inner_frames"][0])
        wire = bytes([len(body) + 4]) + body + b"\x06\x00\x36"
        self.assertEqual(summarize_stream(wire, "server_to_client")["validated_compressed_envelopes"], 1)
        incomplete = b"\xff\xff" + struct.pack("<I", 3) + b"\x30\x07\x15\x36"
        with self.assertRaisesRegex(ValueError, "complete observed frames"):
            compressed_messages(incomplete)


if __name__ == "__main__":
    unittest.main()
