"""Hosted-server tooling: server_env.py (systemd environment file) and pack_server_data.py."""
import argparse
import contextlib
import io
from pathlib import Path
import tarfile
import tempfile
import unittest

from tools import pack_server_data, server_env


def server_data(root):
    for name, body in [('bootstrap/keys/app-key.json', '{}'), ('bootstrap/keys/local-test-key.json', '{}'),
                       ('bootstrap/policy/app-policy.json', '{}'), ('ncguard/fixture.json', '{"rules": []}'),
                       ('terrain/1000/terrain.bin', 'x'), ('navmesh/1000/nav.bin', 'x'),
                       ('game-catalog-v2/manifest.json', '{}'), ('game-catalog-v2/entries.jsonl', '')]:
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding='utf-8')


class ServerEnvTests(unittest.TestCase):
    def args(self, root, **overrides):
        values = dict(public_address='203.0.113.5', bind='0.0.0.0', data=root / 'data', state=root / 'state',
                      proxy=False, certificate=Path('/etc/aion/tls/cert.pem'), key=Path('/etc/aion/tls/key.pem'),
                      max_connections=200, preload=[1000, 1010], geolocation='59.9,10.7,NO', staff_path=None)
        values.update(overrides)
        return argparse.Namespace(**values)

    def test_environment(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            server_data(root / 'data')
            env = server_env.environment(self.args(root), {'aion_app': 'app-secret', 'aion_auth': 'auth-secret'})
        self.assertEqual(env['Aion__Network__PublicAddress'], '203.0.113.5')
        self.assertEqual(env['Aion__Native__Logins'], 'accounts')
        self.assertEqual(env['Aion__NcGuard__Capture'], 'false')
        self.assertEqual(env['Aion__Accounts__Registration'], 'false')  # admins create accounts
        self.assertRegex(env['Aion__Web__StaffPath'], r'^/[a-z0-9]{16}$')
        self.assertIn('Password=app-secret;', env['ConnectionStrings__Aion'])
        self.assertIn('Username=aion_auth;', env['ConnectionStrings__Auth'])
        self.assertNotIn('"', env['ConnectionStrings__Aion'])
        self.assertEqual(env['Aion__Native__PreloadMaps__1'], 1010)
        self.assertIn('Aion__Https__CertificateFile', env)
        self.assertNotIn('Aion__ChatDevelopmentLogins', env)
        self.assertNotIn('Aion__NcGuard__RewriteHosts', env)

    def test_proxy_and_missing_data(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with self.assertRaisesRegex(ValueError, 'Server data incomplete'):
                server_env.environment(self.args(root), {'aion_app': 'a', 'aion_auth': 'b'})
            server_data(root / 'data')
            env = server_env.environment(self.args(root, proxy=True), {'aion_app': 'a', 'aion_auth': 'b'})
        self.assertEqual(env['Aion__Https__Proxy'], 'true')
        self.assertNotIn('Aion__Https__CertificateFile', env)

    def test_public_address_checks(self):
        for address in ('127.0.0.1', '2001:db8::1', 'example.com'):
            with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
                server_env.main(['--public-address', address, '--data', '.', '--state', '.', '--proxy'])

    def test_geolocation(self):
        self.assertEqual(server_env.geolocation('59.9139,10.7522,no'),
                         {'geolocation': 'NO', 'coordinates': {'latitude': '59.9139', 'longitude': '10.7522'}})


class PackServerDataTests(unittest.TestCase):
    def test_archive_layout(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'src'
            server_data(source)
            paths = {'keys': source / 'bootstrap/keys', 'policy': source / 'bootstrap/policy',
                     'ncguard': source / 'ncguard/fixture.json', 'terrain': source / 'terrain',
                     'navmesh': source / 'navmesh', 'catalog': source / 'game-catalog-v2'}
            args = ['--' + name + '=' + str(path) for name, path in paths.items()]
            with contextlib.redirect_stdout(io.StringIO()):
                pack_server_data.main([str(root / 'out.tar.gz'), *args])
            with tarfile.open(root / 'out.tar.gz') as archive:
                names = set(archive.getnames())
            self.assertEqual(names, {'bootstrap/keys/app-key.json', 'bootstrap/keys/local-test-key.json',
                                     'bootstrap/policy/app-policy.json', 'ncguard/fixture.json',
                                     'game-catalog-v2/manifest.json', 'game-catalog-v2/entries.jsonl',
                                     'terrain/1000/terrain.bin', 'navmesh/1000/nav.bin'})
            # The packed layout is what server_env expects.
            unpacked = root / 'data'
            with tarfile.open(root / 'out.tar.gz') as archive:
                archive.extractall(unpacked, filter='data')
            env = server_env.environment(argparse.Namespace(
                public_address='203.0.113.5', bind='0.0.0.0', data=unpacked, state=root / 'state', proxy=True,
                certificate=None, key=None, max_connections=10, preload=[], geolocation='off'), {'aion_app': 'a', 'aion_auth': 'b'})
            self.assertEqual(Path(env['Aion__Native__TerrainDirectory']), (unpacked / 'terrain').resolve())

    def test_wardrobe_members(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in ('wardrobe.json', 'icons/a.webp', 'meshes/m.glb', 'vendor/three/three.module.js',
                         'screenshots/s.png', 'export.log', 'icons/notes.txt'):
                (root / name).parent.mkdir(parents=True, exist_ok=True)
                (root / name).write_text('x', encoding='utf-8')
            names = {name for _, name in pack_server_data.wardrobe_members(root)}
            self.assertEqual(names, {'wardrobe/wardrobe.json', 'wardrobe/icons/a.webp', 'wardrobe/meshes/m.glb',
                                     'wardrobe/vendor/three/three.module.js'})
            server_data(root / 'data')
            (root / 'data/wardrobe').mkdir()
            (root / 'data/wardrobe/wardrobe.json').write_text('{}', encoding='utf-8')
            env = server_env.environment(ServerEnvTests.args(None, root), {'aion_app': 'a', 'aion_auth': 'b'})
            self.assertEqual(Path(env['Aion__WardrobePath']), (root / 'data/wardrobe').resolve())

    def test_missing_settings_key_is_an_error(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp)
            server_data(source)
            (source / 'bootstrap/keys/local-test-key.json').unlink()
            with self.assertRaises(FileNotFoundError):
                pack_server_data.members({'keys': source / 'bootstrap/keys', 'policy': source / 'bootstrap/policy',
                                          'ncguard': source / 'ncguard/fixture.json', 'terrain': source / 'terrain',
                                          'navmesh': source / 'navmesh', 'catalog': source / 'game-catalog-v2'})


if __name__ == '__main__':
    unittest.main()
