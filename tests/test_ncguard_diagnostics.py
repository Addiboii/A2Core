"""Security-envelope logging must remain metadata-only and tolerate unknown layouts."""
import json
import unittest
from tools.aion_protocol import encode_varint
from tools.protocol_knowledge import summarize_body, describe


def specimen(reply=False):
    payload = b'PRIVATE_PAYLOAD!' * 8
    payload = payload.ljust(128, b'!')
    blob = bytearray(bytes.fromhex('1400000000000e001800040008000c00100014000e000000'))
    blob += b'NONCE123'
    blob += (176 - 32).to_bytes(4, 'little')
    blob += (120).to_bytes(4, 'little')
    blob += (4).to_bytes(4, 'little')
    blob += (128).to_bytes(4, 'little') + payload
    blob += (32).to_bytes(4, 'little') + b'PRIVATE_TAG_DATA'.ljust(32, b'!')
    return (b'\x5a\xe2\x00\x00' if reply else b'\x59\xe2') + encode_varint(len(blob)) + blob


class NcguardDiagnosticTests(unittest.TestCase):
    def test_metadata_only_and_no_invented_response_contract(self):
        for reply in (False, True):
            result = summarize_body(specimen(reply))
            self.assertEqual(result['envelope_shape'], 'observed_five_field_layout')
            self.assertEqual(result['payload_bytes'], 128)
            self.assertTrue(result['block_rounding_matches'])
            self.assertNotIn('PRIVATE', json.dumps(result))
            self.assertNotIn('NONCE', json.dumps(result))
            self.assertIsNone(result['expected_replies'])
            self.assertEqual(result['evidence'], 'candidate_struct_mapping')

    def test_truncation_bad_varint_and_unknown_version_do_not_crash(self):
        body = specimen()
        for cut in range(2, len(body)):
            self.assertEqual(summarize_body(body[:cut])['envelope_shape'], 'unrecognized')
        for tail in (b'\xff'*5, b'\x80\x00', b'\x00extra'):
            self.assertFalse(summarize_body(b'\x59\xe2'+tail)['outer_length_valid'])
        changed = bytearray(body)
        changed[4] = 24  # Root offset in the blob: a layout not yet observed.
        result = summarize_body(changed)
        self.assertTrue(result['outer_length_valid'])
        self.assertEqual(result['envelope_shape'], 'unrecognized')

    def test_bad_inner_pointer_is_not_followed(self):
        body = bytearray(specimen())
        body[36:40] = b'\xff'*4  # Inner trailing-vector offset.
        result = summarize_body(body)
        self.assertEqual(result['envelope_shape'], 'unrecognized')
        self.assertNotIn('payload_bytes', result)


if __name__ == '__main__':
    unittest.main()
