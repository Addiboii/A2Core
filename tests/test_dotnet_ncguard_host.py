"""Real TLS/SNI tests with synthetic bytes; no game, hosts edits or trusted CA install."""
import base64
import hashlib
import http.client
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll'


def free_port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


@unittest.skipUnless(HOST.exists() and shutil.which('dotnet'), 'Build Aion.Host first')
class DotnetNcGuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.port, self.http_port = free_port(), free_port()
        self.body = bytes(range(256)) * 185  # larger than the host's normal 16 KB limit
        self.hosts = ['lagavulin.ncsoft.com', 'atp-json.ncsoft.com']
        fixture = {'rules': [{'host': self.hosts[0], 'method': 'GET', 'path': '/def.ec',
                             'body_base64': base64.b64encode(self.body).decode(), 'content_type': 'binary/octet-stream'},
                            {'host': self.hosts[1], 'paths': ['/legacy/'], 'body': 'legacy'}]}
        (self.root / 'fixture.json').write_text(json.dumps(fixture))
        self.stop_file = self.root / 'stop'
        env = {k: v for k, v in os.environ.items() if not k.startswith(('Aion__', 'ConnectionStrings__'))}
        env.update(Aion__LocalPort=str(self.http_port), Aion__NcGuard__Port=str(self.port),
                   Aion__NcGuard__FixtureFile=str(self.root / 'fixture.json'),
                   Aion__NcGuard__DataDirectory=str(self.root), Aion__NcGuard__BodyCap='2048',
                   Aion__NcGuard__StopFile=str(self.stop_file), Aion__NcGuard__DurationSeconds='60',
                   Aion__NcGuard__Hosts__0=self.hosts[0], Aion__NcGuard__Hosts__1=self.hosts[1])
        env['Logging__LogLevel__Microsoft.AspNetCore.Server.Kestrel'] = 'Debug'
        self.log = (self.root / 'host.log').open('w')
        self.process = subprocess.Popen(['dotnet', str(HOST)], cwd=HOST.parent, env=env,
                                        stdout=self.log, stderr=subprocess.STDOUT)
        self.addCleanup(self.stop)
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                self.fail((self.root / 'host.log').read_text())
            try:
                socket.create_connection(('127.0.0.1', self.port), .3).close()
                self.run = next(self.root.glob('tls-mitm-dotnet-*'))
                self.context = ssl.create_default_context(cafile=str(self.run / 'ca.pem'))
                return
            except (OSError, StopIteration):
                time.sleep(.1)
        self.fail('TLS listener did not open')

    def stop(self):
        self.stop_file.touch()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.log.close()

    def request(self, host, method, path, body=b'', host_header=None, verify=True):
        context = self.context if verify else ssl._create_unverified_context()
        with socket.create_connection(('127.0.0.1', self.port), 5) as raw:
            with context.wrap_socket(raw, server_hostname=host) as tls:
                head = (f'{method} {path} HTTP/1.1\r\nHost: {host_header or host}\r\n'
                        f'Content-Length: {len(body)}\r\nX-Test: capture\r\nConnection: close\r\n\r\n').encode()
                tls.sendall(head + body)
                response = http.client.HTTPResponse(tls)
                response.begin()
                return response.status, response.read()

    def test_binary_sni_exact_routes_and_isolation(self):
        self.assertEqual(self.request(self.hosts[0], 'GET', '/def.ec'), (200, self.body))
        self.assertEqual(self.request(self.hosts[1], 'GET', '/legacy/one'), (200, b'legacy'))
        for method, path in [('POST', '/def.ec'), ('GET', '/def.ec?x=1'), ('GET', '/def.ec/extra'),
                             ('GET', '/health/live'), ('GET', '/api/status')]:
            self.assertEqual(self.request(self.hosts[0], method, path)[0], 404)
        self.assertEqual(self.request(self.hosts[1], 'GET', '/def.ec', host_header=self.hosts[0])[0], 404)
        self.assertEqual(self.request('unknown.test', 'GET', '/def.ec', host_header=self.hosts[0], verify=False)[0], 404)
        connection = http.client.HTTPConnection('127.0.0.1', self.http_port)
        connection.request('GET', '/health/live')
        response = connection.getresponse()
        self.assertEqual(response.status, 200)
        response.read(); connection.close()

    def test_large_post_capture_cap_and_graceful_stop(self):
        body = bytes(range(256)) * 500
        self.assertEqual(self.request(self.hosts[0], 'POST', '/', body)[0], 404)
        record = json.loads((self.run / 'mitm-records.jsonl').read_text().splitlines()[-1])
        self.assertEqual(record['received_body_len'], len(body))
        self.assertEqual(record['body_len'], 2048)
        self.assertTrue(record['truncated'])
        self.assertEqual(record['body_sha256'], hashlib.sha256(body[:2048]).hexdigest())
        self.assertEqual((self.run / 'request-bodies.bin').read_bytes(), body[:2048])
        self.assertIn(['X-Test', 'capture'], record['headers'])
        self.stop_file.touch()
        self.assertEqual(self.process.wait(timeout=10), 0)
        state = json.loads((self.run / 'state.json').read_text())
        self.assertEqual(state['status'], 'finished')
        self.assertFalse(state['hosts_file_applied'])
        self.assertEqual(state['hosts'], self.hosts)

