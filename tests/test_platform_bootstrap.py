"""Platform bootstrap routes for title-screen account/security (login-api, login-web)."""
import http.client
import json
import os
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


@unittest.skipUnless(HOST.exists(), 'Build Aion.Host first (AION_SKIP_WEB=1 dotnet build server/Aion.Host)')
class PlatformBootstrapLoginApiTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        fixtures = Path(self.temp.name)
        (fixtures / 'app-key.json').write_text(json.dumps({'value': 'x' * 344}))  # invalid on purpose - skip key routes
        self.port = free_port()
        self.account_port = free_port()
        env = {k: v for k, v in os.environ.items() if not k.startswith(('Aion__', 'ConnectionStrings__'))}
        env.update(Aion__LocalPort=str(self.account_port), Aion__Platform__Port=str(self.port),
                   Aion__SKIP_WEB='1')
        self.process = subprocess.Popen(['dotnet', str(HOST)], cwd=HOST.parent, env=env,
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.addCleanup(self._stop)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                self.fail('host exited early')
            try:
                socket.create_connection(('127.0.0.1', self.port), timeout=0.3).close()
                return
            except OSError:
                time.sleep(0.2)
        self.fail('platform port did not open')

    def _stop(self):
        self.process.terminate()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()

    def request(self, method, path, body=None):
        conn = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        conn.request(method, path, body=body, headers={'Content-Type': 'application/json'} if body else {})
        response = conn.getresponse()
        data = response.read()
        conn.close()
        return response.status, data, response.getheader('Location')

    def test_private_id_and_login_web_redirect(self):
        status, body, _ = self.request('POST', '/local-login-api/account/private-id?token=local-test')
        self.assertEqual(200, status)
        payload = json.loads(body)
        self.assertEqual(64, len(payload['private_id']))
        status, _, location = self.request('GET', '/local-login-web/account')
        self.assertEqual(302, status)
        self.assertTrue(location.startswith(f'http://127.0.0.1:{self.account_port}'))
        status, _, location = self.request('GET', '/local-login-web/security/settings')
        self.assertEqual(302, status)
        self.assertTrue(location.endswith('#totp'))


if __name__ == '__main__':
    unittest.main()
