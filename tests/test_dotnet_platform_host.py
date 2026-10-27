"""End-to-end check of the .NET host's platform bootstrap (port of the HTTP side of
probe_dev_bootstrap.py): fixture routes, a policy encrypted from plaintext and pointed at the
host's own ports, launch codes in the bootstrap URL, a public bind/advertised address, and 503 for
everything else. Uses synthetic fixtures only. Skipped when the host is not built (dotnet build
server/Aion.Host/Aion.Host.csproj; AION_ARTIFACTS_PATH selects an artifacts build).
"""
import base64
import http.client
import json
import os
import shutil
import socket
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

from tests.aes_reference import cbc_decrypt

ROOT = Path(__file__).resolve().parents[1]
HOST_DLL = (ROOT / os.environ['AION_ARTIFACTS_PATH'] / 'bin/Aion.Host/debug/Aion.Host.dll'
            if os.environ.get('AION_ARTIFACTS_PATH') else ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll')
AES_KEY = '0123456789abcdef'
CODE = 'AB' * 32  # launch authorizations are upper-case hex (Convert.ToHexString)


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet') and os.name == 'nt', 'Aion.Host is not built')
class DotnetPlatformHostTests(unittest.TestCase):
    network = {}

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        fixtures = Path(temp.name)
        self.key_value = base64.b64encode(os.urandom(256)).decode()
        (fixtures / 'app-key.json').write_text(json.dumps({'value': self.key_value}))
        (fixtures / 'local-test-key.json').write_text(json.dumps({'key': AES_KEY, 'status': 'test'}))
        (fixtures / 'app-maintenance.json').write_text(json.dumps({'value': base64.b64encode(bytes(16)).decode()}))
        session = {'session': 'local-abc', 'session_secret': 's', 'game_account_id': '1', 'login_name': 'l',
                   'user_id': '1', 'user_name': 'u'}
        (fixtures / 'session-response.candidate.json').write_text(json.dumps(session))
        self.session = session
        (fixtures / 'authn-response.candidate.json').write_text(json.dumps({'authn_token': 'local-dev-token'}))
        (fixtures / 'policy.json').write_text(json.dumps({'api_gate': 'http://127.0.0.1:8087/local-api-gate', 'custom_data': {
            'LoginServerIP': '127.0.0.1:1', 'ChatServerURL': 'http://127.0.0.1:2', 'CommunityUIEnable': 'true'}}))
        (fixtures / 'geo.json').write_text('{"geolocation":"NO"}')
        self.port = free_port()
        self.log = fixtures / 'requests.jsonl'
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Platform__Port=str(self.port),
                   Aion__Platform__FixtureDirectory=str(fixtures),
                   Aion__Platform__PolicyPlaintextFile=str(fixtures / 'policy.json'),
                   Aion__Platform__GeolocationFile=str(fixtures / 'geo.json'), Aion__Platform__RequestLog=str(self.log),
                   Aion__Native__LoginPort=str(free_port()), Aion__Native__Logins='development',
                   Aion__ChatPort=str(free_port()), **self.network)
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        self.env = env
        self.process = subprocess.Popen(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env,
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.addCleanup(self.stop)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                socket.create_connection(('127.0.0.1', self.port), timeout=0.5).close()
                return
            except OSError:
                time.sleep(0.2)
        self.fail('host did not open the platform port')

    def stop(self):
        self.process.terminate()
        self.process.wait(timeout=10)

    def request(self, method, path):
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        connection.request(method, path, body=b'{}' if method == 'POST' else None)
        response = connection.getresponse()
        body = response.read()
        connection.close()
        return response.status, body

    def policy(self, path='/MobileAppConfigSdk/app/policy'):
        status, body = self.request('GET', path)
        self.assertEqual(status, 200)
        key = AES_KEY.encode()
        return json.loads(cbc_decrypt(key, key, base64.b64decode(json.loads(body)['value'])))

    def test_launch_code_routes(self):
        # /c/<code>/...: the same routes, a policy whose api_gate keeps the code, and authn_token
        # redeeming the code (no account service here, so it is rejected, not the fixture token).
        prefix = '/c/' + CODE
        self.assertEqual(self.request('GET', prefix + '/MobileAppConfigSdk/app/key')[0], 200)
        self.assertEqual(self.policy(prefix + '/MobileAppConfigSdk/app/policy')['api_gate'],
                         f'http://127.0.0.1:{self.port}{prefix}/local-api-gate')
        self.assertEqual(self.policy()['api_gate'], f'http://127.0.0.1:{self.port}/local-api-gate')
        self.assertEqual(self.request('GET', prefix + '/local-api-gate/sessions/v1.0/local-abc')[1], json.dumps(self.session).encode())
        self.assertEqual(self.request('GET', prefix + '/local-api-gate/sessions/v1.0/')[0], 200)
        self.assertEqual(self.request('POST', prefix + '/local-api-gate/sessions/v1.0/authn_token')[0], 401)
        self.assertEqual(self.request('GET', '/c/' + 'ab' * 31 + '/MobileAppConfigSdk/app/key')[0], 503)
        log = [json.loads(line) for line in self.log.read_text(encoding='utf-8').splitlines()]
        self.assertTrue(all(CODE not in json.dumps(entry) for entry in log))
        self.assertEqual(log[0]['launch_code'], True)

    def test_fixture_routes(self):
        # Byte-identical to what the Python bootstrap served (json.dumps of the fixture).
        self.assertEqual(self.request('GET', '/MobileAppConfigSdk/app/key?x=1'), (200, json.dumps({'value': self.key_value}).encode()))
        status, body = self.request('GET', '/MobileAppConfigSdk/app/policy')
        key = AES_KEY.encode()
        policy = json.loads(cbc_decrypt(key, key, base64.b64decode(json.loads(body)['value'])))
        self.assertEqual(status, 200)
        self.assertEqual(policy['custom_data']['LoginServerIP'], '127.0.0.1:' + self.env['Aion__Native__LoginPort'])
        self.assertEqual(policy['custom_data']['ChatServerURL'], 'http://127.0.0.1:' + self.env['Aion__ChatPort'])
        self.assertEqual(self.request('POST', '/MobileAppConfigSdk/app/policy/maintenance')[0], 200)
        self.assertEqual(self.request('POST', '/local-api-gate/sessions/v1.0/authn_token')[1],
                         json.dumps({'authn_token': 'local-dev-token'}).encode())
        self.assertEqual(self.request('GET', '/local-api-gate/sessions/v1.0/local-abc')[1], json.dumps(self.session).encode())
        self.assertEqual(self.request('GET', '/local-api-gate/sessions/v1.0/0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b')[0], 200)
        # A fresh saved folder has no stored session ID (the launcher's Saved_A2CorePrivate).
        self.assertEqual(self.request('GET', '/local-api-gate/sessions/v1.0/'), (200, json.dumps(self.session).encode()))
        self.assertEqual(self.request('GET', '/local-api-gate/sessions/v1.0/not-a-session')[0], 503)
        self.assertEqual(self.request('GET', '/local-api-gate/geolocations/v1.0/me'), (200, b'{"geolocation":"NO"}'))
        # Not configured (no secondary-auth fixtures) or unknown: 503, and no static files here.
        self.assertEqual(self.request('POST', '/local-api-gate/secondary_auth/v1.0/my/auth;start')[0], 503)
        self.assertEqual(self.request('GET', '/'), (503, b'{"local_probe":true,"ready":false}'))
        self.assertEqual(self.request('GET', '/MobileAppConfigSdk/app/policy/maintenance')[0], 503)
        log = [json.loads(line) for line in self.log.read_text(encoding='utf-8').splitlines()]
        self.assertEqual(log[0]['query'], '?x=1')
        self.assertIn({'path': '<other>', 'response_status': 503}, [{k: e[k] for k in ('path', 'response_status')} for e in log])


class PublicHostTests(DotnetPlatformHostTests):
    """Bound to every interface, advertising a public address (TLS proxy declared for the account port)."""
    network = {'Aion__Network__BindAddress': '0.0.0.0', 'Aion__Network__PublicAddress': '192.0.2.10',
               'Aion__Https__Proxy': 'true'}

    def test_fixture_routes(self):
        policy = self.policy()
        self.assertEqual(policy['api_gate'], f'http://192.0.2.10:{self.port}/local-api-gate')
        self.assertEqual(policy['custom_data']['LoginServerIP'], '192.0.2.10:' + self.env['Aion__Native__LoginPort'])
        self.assertEqual(policy['custom_data']['ChatServerURL'], 'http://192.0.2.10:' + self.env['Aion__ChatPort'])

    def test_launch_code_routes(self):
        self.assertEqual(self.policy('/c/' + CODE + '/MobileAppConfigSdk/app/policy')['api_gate'],
                         f'http://192.0.2.10:{self.port}/c/{CODE}/local-api-gate')


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet'), 'Aion.Host is not built')
class PublicHostNeedsHttpsTests(unittest.TestCase):
    def test_refuses_plain_account_port(self):
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Network__BindAddress='0.0.0.0',
                   Aion__Network__PublicAddress='192.0.2.10')
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        result = subprocess.run(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env, capture_output=True, text=True, timeout=60)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Aion:Https', result.stdout + result.stderr)

    def test_refuses_loopback_public_address(self):
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Network__BindAddress='0.0.0.0', Aion__Https__Proxy='true')
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        result = subprocess.run(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env, capture_output=True, text=True, timeout=60)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('PublicAddress', result.stdout + result.stderr)


FIXTURES = ROOT / 'research/local/bootstrap-fixtures'


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet') and (FIXTURES / 'rsa-candidate-20261009').exists()
                     and (FIXTURES / 'policy-lime-20261014').exists(), 'Host or research fixtures missing')
class RealFixtureParityTests(unittest.TestCase):
    """The host must serve exactly what probe_dev_bootstrap.py served for the working chain."""

    def test_same_bytes_as_the_python_bootstrap(self):
        keys, policy = FIXTURES / 'rsa-candidate-20261009', FIXTURES / 'policy-lime-20261014'
        port = free_port()
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Platform__Port=str(port),
                   Aion__Platform__FixtureDirectory=str(keys), Aion__Platform__PolicyFile=str(policy / 'app-policy.json'))
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        process = subprocess.Popen(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                try:
                    socket.create_connection(('127.0.0.1', port), timeout=0.5).close()
                    break
                except OSError:
                    time.sleep(0.2)

            def served(method, path):
                connection = http.client.HTTPConnection('127.0.0.1', port, timeout=5)
                connection.request(method, path, body=b'{}' if method == 'POST' else None)
                body = connection.getresponse().read()
                connection.close()
                return body

            def python(path):  # probe_dev_bootstrap: json.dumps(json.loads(fixture))
                return json.dumps(json.loads(path.read_text(encoding='utf-8'))).encode()
            def decrypted(body):
                key = json.loads((keys / 'local-test-key.json').read_text(encoding='utf-8'))['key'].encode()
                return json.loads(cbc_decrypt(key, key, base64.b64decode(json.loads(body)['value'])))
            expected = decrypted((policy / 'app-policy.json').read_bytes())
            for name in ('api_gate', 'login_web', 'login_api'):
                expected[name] = expected[name].replace('127.0.0.1:8087', f'127.0.0.1:{port}')
            self.assertEqual(decrypted(served('GET', '/MobileAppConfigSdk/app/policy')), expected)
            session = json.loads((keys / 'session-response.candidate.json').read_text(encoding='utf-8'))['session']
            for method, route, fixture in [
                    ('GET', '/MobileAppConfigSdk/app/key', keys / 'app-key.json'),
                    ('POST', '/MobileAppConfigSdk/app/policy/maintenance', keys / 'app-maintenance.json'),
                    ('GET', '/local-api-gate/sessions/v1.0/' + session, keys / 'session-response.candidate.json'),
                    ('POST', '/local-api-gate/sessions/v1.0/authn_token', keys / 'authn-response.candidate.json'),
                    ('POST', '/local-api-gate/secondary_auth/v1.0/my/auth;start', keys / 'secondary-start-response.candidate.json'),
                    ('POST', '/local-api-gate/secondary_auth/v1.0/my/auth;finish', keys / 'secondary-finish-response.candidate.json')]:
                self.assertEqual(served(method, route), python(fixture), route)
        finally:
            process.terminate()
            process.wait(timeout=10)


if __name__ == '__main__':
    unittest.main()
