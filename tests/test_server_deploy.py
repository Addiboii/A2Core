"""Hosted-server tooling: server_env.py (systemd environment file) and pack_server_data.py."""
import argparse
import contextlib
import io
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch

from tools import pack_server_data, server_env

ROOT = Path(__file__).resolve().parents[1]


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

    def test_totp_key_and_log_directory(self):
        import base64
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            server_data(root / 'data')
            env = server_env.environment(self.args(root), {'aion_app': 'a', 'aion_auth': 'b'})
            self.assertEqual(len(base64.b64decode(env['Aion__Totp__EncryptionKey'])), 32)
            kept = base64.b64encode(bytes(range(32))).decode()
            env = server_env.environment(self.args(root, totp_key=kept), {'aion_app': 'a', 'aion_auth': 'b'})
            self.assertEqual(env['Aion__Totp__EncryptionKey'], kept)  # kept across updates
            self.assertEqual(env['Aion__Logging__File__Directory'], root / 'state' / 'logs')
            self.assertNotIn('Aion__Logging__File__Enabled', env)  # log files stay off by default
        with self.assertRaises(SystemExit):
            server_env.main(['--public-address', '203.0.113.5', '--data', '.', '--state', '.', '--proxy', '--totp-key', 'c2hvcnQ='])

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

    def test_loopback_needs_a_loopback_bind(self):
        # One PC (deploy/windows -Local): players and server on 127.0.0.1; never a public bind with a loopback address.
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            server_env.main(['--public-address', '127.0.0.1', '--data', '.', '--state', '.', '--proxy'])
        error = io.StringIO()
        with contextlib.redirect_stderr(error), contextlib.redirect_stdout(io.StringIO()):
            try:
                server_env.main(['--public-address', '127.0.0.1', '--bind', '127.0.0.1', '--data', 'missing', '--state', '.', '--proxy'])
            except (SystemExit, OSError):
                pass                                          # the missing data or credentials, after the address check
        self.assertNotIn('non-loopback', error.getvalue())

    def test_install_passes_open_registration(self):
        script = (ROOT / 'deploy/linux/install.sh').read_text(encoding='utf-8')
        self.assertIn('--open-registration) OPEN_REGISTRATION=1', script)
        self.assertIn('TLS_ARGS+=(--open-registration)', script)

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


DEPLOY = Path(__file__).resolve().parents[1] / 'deploy/linux'


class LocalSettingsTests(unittest.TestCase):
    def test_rates_live_in_a_file_updates_keep(self):
        service = (DEPLOY / 'aion-host.service').read_text(encoding='utf-8')
        # Read after aion.env (later lines win) and optional ("-": the host starts without it).
        self.assertLess(service.index('EnvironmentFile=/etc/aion/aion.env'), service.index('EnvironmentFile=-/etc/aion/local.env'))
        install = (DEPLOY / 'install.sh').read_text(encoding='utf-8')
        self.assertIn('if [[ ! -f $CONFIG/local.env ]]; then', install)
        for key in ('Experience', 'Kinah', 'Drops'):
            self.assertIn(f'#Aion__Rates__{key}=1', install)
            self.assertIn(f'Aion__Rates__', (DEPLOY / 'README.md').read_text(encoding='utf-8'))


def posix_bash():
    """Git Bash on Windows (never WSL's bash.exe, which sees other paths), else the system bash."""
    if os.name == 'nt':
        candidate = Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'Git/bin/bash.exe'
        return str(candidate) if candidate.exists() else None
    return shutil.which('bash')


FAKE_POSTGRES_DEV = """import sys
from pathlib import Path
if sys.argv[1:2] == ['backup']:
    target = Path(sys.argv[2])
    if target.parent.joinpath('FAIL').exists():
        target.write_text('half')
        sys.exit('pg_dump failed')
    target.write_text('dump')
"""


class BackupScriptTests(unittest.TestCase):
    def test_scripts_parse(self):
        bash = posix_bash()
        if not bash:
            self.skipTest('no bash')
        for name in ('install.sh', 'update.sh', 'aion-backup.sh', 'aion-restore.sh'):
            result = subprocess.run([bash, '-n', str(DEPLOY / name)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, f'{name}: {result.stderr}')

    def run_backup(self, folder, keep_days=None):
        env = {**os.environ, 'AION_BACKUP_DIR': folder.joinpath('backups').as_posix(), 'AION_TOOLS': folder.joinpath('tools').as_posix(),
               'AION_PYTHON': Path(sys.executable).as_posix(), 'AION_PG_BIN': '/nonexistent', 'AION_PG_HOME': folder.as_posix()}
        if keep_days is not None:
            env['AION_BACKUP_KEEP_DAYS'] = keep_days
        return subprocess.run([posix_bash(), (DEPLOY / 'aion-backup.sh').as_posix()], capture_output=True, text=True, env=env)

    def test_nightly_backup_keeps_the_configured_days(self):
        if not posix_bash():
            self.skipTest('no bash')
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            (folder / 'tools').mkdir()
            (folder / 'tools/postgres_dev.py').write_text(FAKE_POSTGRES_DEV, encoding='utf-8')
            backups = folder / 'backups'
            backups.mkdir()
            day = 86400
            for name, age in [('nightly-old.dump', 20 * day), ('nightly-edge.dump', 7 * day + 600), ('nightly-recent.dump', 6 * day),
                              ('before-update-old.dump', 90 * day), ('aion-dashboard.dump', 90 * day)]:
                path = backups / name
                path.write_text('x')
                os.utime(path, (time.time() - age, time.time() - age))
            result = self.run_backup(folder, '7')
            self.assertEqual(result.returncode, 0, result.stderr)
            names = sorted(p.name for p in backups.iterdir())
            self.assertNotIn('nightly-old.dump', names)
            self.assertNotIn('nightly-edge.dump', names)       # 7 days old with 7 kept: goes
            self.assertIn('nightly-recent.dump', names)
            self.assertIn('before-update-old.dump', names)     # other backups follow their own rules
            self.assertIn('aion-dashboard.dump', names)
            made = [n for n in names if n.startswith('nightly-2')]
            self.assertEqual(len(made), 1)
            self.assertEqual((backups / made[0]).read_text(), 'dump')
            self.assertIn('Removed old backup', result.stdout)
            # A failed dump leaves no file (not even the partial one) and removes nothing.
            (backups / 'FAIL').write_text('')
            os.utime(backups / 'nightly-recent.dump', (time.time() - 30 * day, time.time() - 30 * day))
            failed = self.run_backup(folder, '7')
            self.assertNotEqual(failed.returncode, 0)
            (backups / 'FAIL').unlink()
            self.assertEqual(sorted(p.name for p in backups.iterdir()), names)
            # The days kept must be a whole number, 1 or more.
            for bad in ('0', 'two', '-3'):
                self.assertEqual(self.run_backup(folder, bad).returncode, 2)

    def test_install_and_units(self):
        install = (DEPLOY / 'install.sh').read_text(encoding='utf-8')
        for line in ('install -m 755 "$REPO/deploy/linux/aion-backup.sh" /usr/local/sbin/aion-backup',
                     'install -m 755 "$REPO/deploy/linux/aion-restore.sh" /usr/local/sbin/aion-restore',
                     'systemctl enable --now aion-backup.timer', '$STATE/backups'):
            self.assertIn(line, install)
        service = (DEPLOY / 'aion-backup.service').read_text(encoding='utf-8')
        self.assertIn('ExecStart=/usr/local/sbin/aion-backup', service)
        self.assertIn('User=aion', service)
        self.assertIn('ReadWritePaths=/var/lib/aion/backups', service)
        self.assertIn('EnvironmentFile=-/etc/aion/local.env', service)
        timer = (DEPLOY / 'aion-backup.timer').read_text(encoding='utf-8')
        self.assertIn('OnCalendar=', timer)
        self.assertIn('Persistent=true', timer)
        # update.sh's newest-10 pruning leaves nightly dumps to the timer's retention.
        self.assertIn("grep -v '/nightly-'", (DEPLOY / 'update.sh').read_text(encoding='utf-8'))
        restore = (DEPLOY / 'aion-restore.sh').read_text(encoding='utf-8')
        self.assertIn('read -r -p', restore)
        self.assertIn('restore "$FILE" --confirm-database aion', restore)
        self.assertLess(restore.index('verify-backup'), restore.index('systemctl stop aion-host'))
        self.assertLess(restore.index('as_aion backup "$SAFETY"'), restore.index('restore "$FILE"'))

    def test_restore_needs_the_database_named(self):
        from tools import postgres_dev
        calls = []
        with tempfile.NamedTemporaryFile(suffix='.dump', delete=False) as dump:
            path = Path(dump.name)
        try:
            with patch.object(postgres_dev, 'run', lambda *a, **kw: calls.append((a, kw))):
                with self.assertRaises(SystemExit):
                    postgres_dev.restore(path, 'other')
                with self.assertRaises(SystemExit):
                    postgres_dev.restore(path.with_name('missing-backup.dump'), postgres_dev.DATABASE)
                self.assertEqual(calls, [])
                postgres_dev.restore(path, postgres_dev.DATABASE)
        finally:
            path.unlink()
        (binary, args), kw = calls[0]
        self.assertEqual(binary, 'pg_restore')
        self.assertIn('--single-transaction', args)
        self.assertIn('--clean', args)
        self.assertEqual(kw['timeout'], postgres_dev.long_timeout())


class WindowsInstallTests(unittest.TestCase):
    WINDOWS = ROOT / 'deploy/windows'

    def test_postgres_download_is_pinned_to_the_tool_archive(self):
        # install.ps1 fetches the archive postgres_dev.py setup unpacks on Windows, and checks its SHA-256.
        script = (self.WINDOWS / 'install.ps1').read_text(encoding='utf-8')
        tool = (ROOT / 'tools/postgres_dev.py').read_text(encoding='utf-8')
        archive = 'postgresql-18.6-1-windows-x64-binaries.zip'
        self.assertIn(archive, script)
        self.assertIn(archive, tool)
        self.assertRegex(script, r"\$PgSha256 = '[0-9a-f]{64}'")
        self.assertIn('Get-FileHash $archive -Algorithm SHA256', script)

    def test_install_repeats_the_linux_steps(self):
        script = (self.WINDOWS / 'install.ps1').read_text(encoding='utf-8')
        for step in ("'setup'", "'import-catalog'", 'configure_starters.py', "'install'", 'equipment_rules.py',
                     "'upgrade-catalog'", "'backup'", 'server_env.py', '--staff-path', '--totp-key', '--open-registration'):
            self.assertIn(step, script)
        # The game's ports, as install.sh opens them (plus the account port).
        self.assertIn('$Ports = 443, 3700, 8087, 8091, 8780, 13328', script)

    @unittest.skipUnless(os.name == 'nt' and shutil.which('powershell'), 'needs Windows PowerShell')
    def test_scripts_parse(self):
        for name in ('install.ps1', 'start.ps1', 'stop.ps1'):
            command = ("$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('%s', [ref]$null, [ref]$errors); "
                       "if ($errors) { $errors | ForEach-Object { $_.Message }; exit 1 }") % str(self.WINDOWS / name)
            result = subprocess.run(['powershell', '-NoProfile', '-Command', command], capture_output=True, text=True, timeout=60)
            self.assertEqual(result.returncode, 0, name + ': ' + result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
