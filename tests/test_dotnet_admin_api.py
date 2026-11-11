"""The built host's admin API gate without a database: account services configured against an address where no
PostgreSQL listens, so every request that would need the database fails, and the checks below are the ones the host
answers before it (staff sessions only on /api/admin, migration 075). Skipped when the host is not built."""
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

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll'


def free_port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


@unittest.skipUnless(HOST.exists() and shutil.which('dotnet'), 'Build Aion.Host first')
class DotnetAdminApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        root = Path(cls.temp.name)
        (root / 'wardrobe').mkdir()
        (root / 'wardrobe/wardrobe.json').write_text('{"items":[]}')
        cls.port = free_port()
        dead = f'Host=127.0.0.1;Port={free_port()};Database=none;Username=none;Password=none;Timeout=2'
        env = {k: v for k, v in os.environ.items() if not k.startswith(('Aion__', 'ConnectionStrings__'))}
        env.update(Aion__LocalPort=str(cls.port), ConnectionStrings__Aion=dead, ConnectionStrings__Auth=dead,
                   Aion__WardrobePath=str(root / 'wardrobe'), Aion__Wardrobe__StaffOnly='true')
        cls.log = (root / 'host.log').open('w')
        cls.process = subprocess.Popen(['dotnet', str(HOST)], cwd=HOST.parent, env=env, stdout=cls.log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if cls.process.poll() is not None:
                cls.log.close()
                raise AssertionError((root / 'host.log').read_text())
            try:
                socket.create_connection(('127.0.0.1', cls.port), .3).close()
                return
            except OSError:
                time.sleep(.1)
        raise AssertionError('host did not open its port')

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        try:
            cls.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            cls.process.kill()
            cls.process.wait()
        cls.log.close()
        cls.temp.cleanup()

    def request(self, method, path, token=None, body=None):
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=10)
        headers = {'Authorization': 'Bearer ' + token} if token else {}
        if body is not None:
            headers['Content-Type'] = 'application/json'
        connection.request(method, path, body=json.dumps(body) if body is not None else None, headers=headers)
        response = connection.getresponse()
        data = response.read()
        connection.close()
        return response.status, data, dict(response.getheaders())

    def test_admin_api_needs_a_staff_session(self):
        # No bearer, or one that is no session token at all: refused before any handler or database call.
        for method, path, body in [('GET', '/api/admin/me', None), ('GET', '/api/admin/characters?q=a', None),
                                   ('POST', '/api/admin/commands', {}), ('POST', '/api/admin/accounts', {}),
                                   ('POST', '/api/admin/wardrobe-access', None), ('GET', '/api/admin/no-such-route', None),
                                   ('POST', f'/api/admin/accounts/{"0" * 8}-0000-0000-0000-{"0" * 12}/roles', {'role': 'admin', 'granted': True})]:
            for token in (None, 'not-a-session', 'A' * 63):
                status, data, headers = self.request(method, path, token, body)
                self.assertEqual(status, 401, (method, path, token))
                self.assertEqual(json.loads(data), {'error': 'staff_session_required'})
                self.assertEqual(headers.get('Cache-Control'), 'no-store')

    def test_wardrobe_sign_out_needs_no_session(self):
        status, _, _ = self.request('DELETE', '/api/admin/wardrobe-access')
        self.assertEqual(status, 204)

    def test_player_endpoints_are_not_gated(self):
        # The account page's own calls are not under /api/admin: a missing bearer is their own 401, not the gate's.
        status, data, _ = self.request('GET', '/api/accounts/me')
        self.assertEqual(status, 401)
        self.assertNotIn(b'staff_session_required', data)


if __name__ == '__main__':
    unittest.main()
