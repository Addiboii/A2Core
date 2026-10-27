import http.client
from http.server import HTTPServer
import threading
import unittest
import json
import os
from pathlib import Path
import subprocess
import tempfile

from tools.build_settings_key_fixture import encrypt
from tools.probe_local_bootstrap import handler


class SettingsKeyTests(unittest.TestCase):
    def test_secondary_finish_is_opt_in_and_exact_post(self):
        base = '/local-api-gate/secondary_auth/v1.0/my/'
        candidate = b'{"token":"synthetic-secondary-token"}'
        for fixture in (None, candidate):
            events = []
            with HTTPServer(('127.0.0.1', 0), handler(events, secondary_finish_response=fixture,
                                                    record_route_paths=True)) as server:
                for method, suffix in [('POST', 'auth;finish'), ('GET', 'auth;finish'),
                                       ('POST', 'auth;finish/extra'), ('POST', 'auth;start'),
                                       ('POST', 'devices/register;finish')]:
                    worker = threading.Thread(target=server.handle_request)
                    worker.start()
                    conn = http.client.HTTPConnection(*server.server_address, timeout=3)
                    try:
                        conn.request(method, base + suffix + '?private=query',
                                     body=b'private-body', headers={'Authorization': 'private-header'})
                        response = conn.getresponse()
                        accepted = fixture is not None and method == 'POST' and suffix == 'auth;finish'
                        self.assertEqual(response.status, 200 if accepted else 503)
                        body = response.read()
                        if accepted:
                            self.assertEqual(body, candidate)
                    finally:
                        conn.close()
                        worker.join(timeout=4)
                self.assertNotIn('private', json.dumps(events))
                self.assertNotIn('synthetic-secondary-token', json.dumps(events))

    def test_secondary_start_is_opt_in_and_does_not_complete_auth(self):
        base = '/local-api-gate/secondary_auth/v1.0/my/'
        candidate = b'{"session_id":"local-secondary-test"}'
        for fixture in (None, candidate):
            events = []
            with HTTPServer(('127.0.0.1', 0), handler(events, secondary_start_response=fixture,
                                                    record_route_paths=True)) as server:
                cases = [('POST', 'auth;start'), ('GET', 'auth;start'),
                         ('POST', 'auth;start/extra'), ('POST', 'auth;finish'),
                         ('POST', 'devices/register;start')]
                for method, suffix in cases:
                    worker = threading.Thread(target=server.handle_request)
                    worker.start()
                    conn = http.client.HTTPConnection(*server.server_address, timeout=3)
                    try:
                        conn.request(method, base + suffix + '?private=query',
                                     body=b'private-body', headers={'Authorization': 'private-header'})
                        response = conn.getresponse()
                        accepted = fixture is not None and method == 'POST' and suffix == 'auth;start'
                        self.assertEqual(response.status, 200 if accepted else 503)
                        body = response.read()
                        if accepted:
                            self.assertEqual(body, candidate)
                    finally:
                        conn.close()
                        worker.join(timeout=4)
                self.assertNotIn('private', json.dumps(events))

    def test_token_response_only_exact_post(self):
        events = []
        base = '/local-api-gate/sessions/v1.0/'
        with HTTPServer(('127.0.0.1', 0), handler(events, authn_response=b'local-token')) as server:
            for method, suffix, expected in [('POST', 'authn_token', 200), ('GET', 'authn_token', 503),
                                             ('POST', 'authn_token/extra', 503), ('POST', 'guest_token', 503)]:
                worker = threading.Thread(target=server.handle_request)
                worker.start()
                conn = http.client.HTTPConnection(*server.server_address, timeout=3)
                try:
                    conn.request(method, base+suffix)
                    response = conn.getresponse()
                    self.assertEqual(response.status, expected)
                    body = response.read()
                    if expected == 200:
                        self.assertEqual(body, b'local-token')
                finally:
                    conn.close()
                    worker.join(timeout=4)

    def test_issued_non_uuid_session_can_be_resumed(self):
        events = []
        issued = 'local-0123456789abcdef'
        with HTTPServer(('127.0.0.1', 0), handler(events, session_response=b'synthetic',
                                                session_identifier=issued)) as server:
            for method, suffix, expected in [('GET', issued, 200), ('GET', issued+'x', 503),
                                             ('GET', issued+'/extra', 503), ('POST', issued, 503),
                                             ('GET', 'authn_token', 503)]:
                worker = threading.Thread(target=server.handle_request)
                worker.start()
                conn = http.client.HTTPConnection(*server.server_address, timeout=3)
                try:
                    conn.request(method, '/local-api-gate/sessions/v1.0/'+suffix)
                    response = conn.getresponse()
                    self.assertEqual(response.status, expected)
                    response.read()
                finally:
                    conn.close()
                    worker.join(timeout=4)

    def test_session_candidate_only_exact_uuid_get(self):
        events = []
        base = '/local-api-gate/sessions/v1.0/'
        identifier = '01234567-89ab-cdef-0123-456789abcdef'
        cases = [('GET', identifier, 200), ('POST', identifier, 503),
                 ('GET', identifier+'/extra', 503), ('GET', 'authn_token', 503),
                 ('GET', 'sessions', 503), ('GET', '', 503)]
        with HTTPServer(('127.0.0.1', 0), handler(events, session_response=b'synthetic')) as server:
            for method, suffix, expected in cases:
                worker = threading.Thread(target=server.handle_request)
                worker.start()
                conn = http.client.HTTPConnection(*server.server_address, timeout=3)
                try:
                    conn.request(method, base+suffix)
                    response = conn.getresponse()
                    self.assertEqual(response.status, expected)
                    body = response.read()
                    if expected == 200:
                        self.assertEqual(body, b'synthetic')
                finally:
                    conn.close()
                    worker.join(timeout=4)

    def test_maintenance_only_post_exact_route(self):
        events = []
        with HTTPServer(('127.0.0.1', 0), handler(events, maintenance_response=b'maintenance')) as server:
            for method, suffix, expected in [('POST', '', 200), ('GET', '', 503), ('POST', '/extra', 503)]:
                worker = threading.Thread(target=server.handle_request)
                worker.start()
                conn = http.client.HTTPConnection(*server.server_address, timeout=3)
                try:
                    conn.request(method, '/MobileAppConfigSdk/app/policy/maintenance'+suffix)
                    response = conn.getresponse()
                    self.assertEqual(response.status, expected)
                    body = response.read()
                    if expected == 200:
                        self.assertEqual(body, b'maintenance')
                finally:
                    conn.close()
                    worker.join(timeout=4)

    def test_opt_in_path_capture_excludes_query(self):
        events = []
        with HTTPServer(('127.0.0.1', 0), handler(events, record_route_paths=True)) as server:
            worker = threading.Thread(target=server.handle_request)
            worker.start()
            conn = http.client.HTTPConnection(*server.server_address, timeout=3)
            try:
                conn.request('POST', '/local-api-gate/launch?token=secret', body=b'private-body',
                             headers={'Authorization': 'private-header'})
                response = conn.getresponse()
                response.read()
            finally:
                conn.close()
                worker.join(timeout=4)
        self.assertEqual(events[0]['route_path'], '/local-api-gate/launch')
        self.assertNotIn('secret', json.dumps(events))
        self.assertNotIn('private-', json.dumps(events))

    def test_rsa_roundtrip_and_padding(self):
        # Known Mersenne primes give an independent small test-only private key.
        p, q = 2**127 - 1, 2**107 - 1
        n, e = p*q, 65537
        d = pow(e, -1, (p-1)*(q-1))
        message = b'0123456789abcdef'
        ciphertext = encrypt(message, n, e)
        block = pow(int.from_bytes(ciphertext, 'big'), d, n).to_bytes(len(ciphertext), 'big')
        self.assertEqual(block[:2], b'\x00\x02')
        padding, result = block[2:].split(b'\x00', 1)
        self.assertGreaterEqual(len(padding), 8)
        self.assertEqual(result, message)
        with self.assertRaises(ValueError):
            encrypt(b'x'*20, n, e)

    def test_only_key_get_receives_candidate(self):
        events = []
        fixture = b'{"value":"candidate"}'
        with HTTPServer(('127.0.0.1', 0), handler(events, fixture)) as server:
            for method, path, expected in [('GET', '/MobileAppConfigSdk/app/key', 200),
                                           ('GET', '/MobileAppConfigSdk/app/policy', 503),
                                           ('POST', '/MobileAppConfigSdk/app/key', 503)]:
                worker = threading.Thread(target=server.handle_request)
                worker.start()
                conn = http.client.HTTPConnection(*server.server_address, timeout=3)
                try:
                    conn.request(method, path)
                    response = conn.getresponse()
                    self.assertEqual(response.status, expected)
                    body = response.read()
                    if expected == 200:
                        self.assertEqual(body, fixture)
                finally:
                    conn.close()
                    worker.join(timeout=4)
                self.assertFalse(worker.is_alive())
        self.assertEqual([event['response_status'] for event in events], [200, 503, 503])

    def test_policy_candidate_only_on_policy_get(self):
        events = []
        with HTTPServer(('127.0.0.1', 0), handler(events, b'key', b'policy')) as server:
            for method, path, status, expected in [
                ('GET', '/MobileAppConfigSdk/app/policy', 200, b'policy'),
                ('POST', '/MobileAppConfigSdk/app/policy', 503, None),
                ('GET', '/local-login-api', 503, None),
                ('POST', '/local-login-api/account/private-id?token=secret', 503, None)]:
                worker = threading.Thread(target=server.handle_request)
                worker.start()
                conn = http.client.HTTPConnection(*server.server_address, timeout=3)
                try:
                    conn.request(method, path)
                    response = conn.getresponse()
                    self.assertEqual(response.status, status)
                    body = response.read()
                    if expected:
                        self.assertEqual(body, expected)
                finally:
                    conn.close()
                    worker.join(timeout=4)
        self.assertEqual(events[-1]['path'], '<other>')
        self.assertEqual(events[-1]['route_family'], '/local-login-api')
        self.assertNotIn('private-id', json.dumps(events))
        self.assertNotIn('secret', json.dumps(events))

    @unittest.skipUnless(os.name == 'nt', 'Windows .NET AES fixture builder')
    def test_policy_builder_roundtrip(self):
        root = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as temp:
            temp = Path(temp)
            (temp/'key.json').write_text(json.dumps({'key': '0123456789abcdef'}))
            text = '{"login_api":"http://127.0.0.1:8087/local-login-api"}'
            (temp/'policy.json').write_text(text)
            args = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    str(root/'tools/build_settings_policy_fixture.ps1'), '-KeyFile', str(temp/'key.json'),
                    '-PolicyFile', str(temp/'policy.json'), '-OutputFile', str(temp/'out.json')]
            subprocess.run(args, check=True, capture_output=True)
            script = '''param($fixture)
$value = (Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json).value
$aes = [Security.Cryptography.Aes]::Create()
try {
 $aes.Key = [Text.Encoding]::ASCII.GetBytes('0123456789abcdef'); $aes.IV = $aes.Key
 $bytes = [Convert]::FromBase64String($value); $dec = $aes.CreateDecryptor()
 try { [Console]::Write([Text.Encoding]::UTF8.GetString($dec.TransformFinalBlock($bytes,0,$bytes.Length))) }
 finally { $dec.Dispose() }
} finally { $aes.Dispose() }
'''
            (temp/'decrypt.ps1').write_text(script)
            result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(temp/'decrypt.ps1'),
                                     str(temp/'out.json')], check=True, capture_output=True, text=True)
            self.assertEqual(result.stdout, text)
            self.assertNotEqual(subprocess.run(args, capture_output=True).returncode, 0)
