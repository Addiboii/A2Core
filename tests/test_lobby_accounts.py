import json
import unittest
from tools.inspect_lobby_accounts import account_shape


def fixture():
    def text(s):
        b=s.encode(); return bytes([len(b)])+b
    return (b'\x06\x39\0\0\0'+text('11111111-1111-1111-1111-111111111111')+
            b'\x01\0\0'+text('123456789:22222222-2222-2222-2222-222222222222')+
            (1204).to_bytes(2,'little')+b'\x01\0\0\0\0'+
            (1204).to_bytes(2,'little')+b'\x03\x09\x03')


class AccountTests(unittest.TestCase):
    def test_shape_and_redaction(self):
        result=account_shape(fixture())
        self.assertEqual(result['session_guid_shapes'],[True,True])
        self.assertEqual(result['server_candidate'],1204)
        rendered=json.dumps(result)
        self.assertNotIn('123456789',rendered)
        self.assertNotIn('11111111-',rendered)
        self.assertNotIn('22222222-',rendered)

    def test_every_truncation_rejected(self):
        body=fixture()
        for n in range(len(body)):
            with self.subTest(n=n),self.assertRaises(ValueError):
                account_shape(body[:n])

    def test_inconsistent_server_and_trailing_data(self):
        body=bytearray(fixture());body[-5]^=1
        for invalid in (bytes(body),fixture()+b'\0',fixture().replace(b':',b'!')):
            with self.assertRaises(ValueError):account_shape(invalid)
