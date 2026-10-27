import unittest
from tools.probe_login_reply import (RC4, build_reply, derive_key, encode_varint,
                                     oaep_encode, rc4, rsa_pub_encrypt,
                                     rsa_public_from_hello, summarize_followup)
from tools.aion_protocol import Reader, der_tlv, split_frames


class LoginReplyProbeTests(unittest.TestCase):
    def test_varint_roundtrip(self):
        for value in (0, 6, 127, 128, 281, 16384, 0xFFFFFFFF):
            encoded = encode_varint(value)
            from tools.aion_protocol import varuint
            self.assertEqual(varuint(encoded, 0), (value, len(encoded)))

    def test_rc4_matches_reference(self):
        # Known RC4 test vector: key and plaintext are all zero bytes.
        self.assertEqual(
            rc4(b"\x00" * 8, b"\x00" * 8),
            bytes.fromhex("de188941a3375d3a"))

    def test_rc4_stream_equals_whole(self):
        key = b"local-login-probe-key"
        body_a = b"\x02\x39\xaa\xbb\xcc"
        body_b = b"\x03\x39\xdd\xee"
        stream = RC4(key)
        streamed = stream.crypt(body_a) + stream.crypt(body_b)
        self.assertEqual(streamed, rc4(body_a + body_b, key))

    def test_build_reply_parses_as_0x3901_frame(self):
        # A 2048-bit-shaped modulus (no decryption needed) validates structure.
        n = (1 << 2047) + 1
        e = 3
        reply = build_reply(n, e, b"\x01" * 16, parameter=60)
        frames, consumed = split_frames(reply)
        self.assertEqual(consumed, len(reply))
        body = frames[0].body
        self.assertEqual(int.from_bytes(body[:2], "little"), 0x3901)
        self.assertEqual(int.from_bytes(body[2:4], "little"), 0)
        self.assertEqual(int.from_bytes(body[4:6], "little"), 60)

    def test_oaep_capacity(self):
        with self.assertRaises(ValueError):
            oaep_encode(b"x" * 220, 256)  # too long for OAEP-SHA1 2048-bit
        self.assertEqual(len(oaep_encode(b"x" * 16, 256)), 256)

    def test_rsa_public_from_hello(self):
        import struct
        # Synthetic 0x3900 body with a DER RSAPublicKey (small toy key).
        n = 2**127 - 1
        e = 3
        def der_int(value):
            raw = value.to_bytes((value.bit_length() + 7) // 8, "big")
            return b"\x02" + bytes([len(raw)]) + raw
        der = der_int(n) + der_int(e)
        der = b"\x30" + bytes([len(der)]) + der
        body = b"\x00\x39" + encode_varint(len(der)) + der + b"\x00\x00\x00"
        modulus, exponent = rsa_public_from_hello(body)
        self.assertEqual(modulus, n)
        self.assertEqual(exponent, e)

    def test_summarize_followup_reports_both_rc4_modes(self):
        key = b"local-login-probe-key"
        plaintext_body = b"\x02\x39" + b"x" * 9
        encrypted_body = rc4(plaintext_body, key)
        logical = len(encrypted_body) + 4
        followup = encode_varint(logical) + encrypted_body
        summary = summarize_followup(followup, key)
        self.assertEqual(summary["received_bytes"], len(followup))
        self.assertEqual(summary["raw_frame_parse"]["frame_count"], 1)
        self.assertEqual(summary["body_rc4_stream_headers"][0], "0x3902")
        self.assertEqual(summary["body_rc4_per_frame_headers"][0], "0x3902")


if __name__ == "__main__":
    unittest.main()
