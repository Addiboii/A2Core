import base64
import binascii
import unittest

from tools.loopback_tls_mitm import respond_for


class ResponseFixturesTest(unittest.TestCase):
    def setUp(self):
        self.body = bytes(range(256)) * 4
        self.fixture = {'rules': [{'host': 'example.test', 'method': 'GET',
            'path': '/def.ec.key', 'content_type': 'binary/octet-stream',
            'body_base64': base64.b64encode(self.body).decode('ascii')}]}

    def test_binary_response_preserves_every_byte(self):
        status, _, headers, body = respond_for('example.test', 'GET /def.ec.key HTTP/1.1', self.fixture)
        self.assertEqual(status, 200)
        self.assertEqual(headers['Content-Type'], 'binary/octet-stream')
        self.assertEqual(body, self.body)

    def test_exact_route_and_method_leave_other_requests_unanswered(self):
        for host, request in [('other.test', 'GET /def.ec.key HTTP/1.1'),
                              ('example.test', 'POST /def.ec.key HTTP/1.1'),
                              ('example.test', 'GET /def.ec.key/extra HTTP/1.1'),
                              ('example.test', 'GET /def.ec.key?extra=1 HTTP/1.1')]:
            with self.subTest(host=host, request=request):
                self.assertEqual(respond_for(host, request, self.fixture)[0], 404)

    def test_invalid_binary_encoding_is_rejected(self):
        self.fixture['rules'][0]['body_base64'] = '%%invalid%%'
        with self.assertRaises(binascii.Error):
            respond_for('example.test', 'GET /def.ec.key HTTP/1.1', self.fixture)

    def test_legacy_text_prefix_fixture(self):
        fixture = {'rules': [{'host': 'example.test', 'paths': ['/old/'], 'body': 'text'}]}
        self.assertEqual(respond_for('example.test', 'GET /old/resource HTTP/1.1', fixture)[3], b'text')
