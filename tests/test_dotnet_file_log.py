"""The host's optional log files (Aion:Logging:File, server/Aion.Host/Logging/FileLogging.cs).

Starts the built Aion.Host without databases: with log files on, lines reach server.log and rotate
by size; with an unusable directory (or one inside the host folder) the host still starts. Skipped
when the host is not built (dotnet build server/Aion.Host).
"""
import os
import shutil
import socket
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HOST_DLL = (ROOT / os.environ['AION_ARTIFACTS_PATH'] / 'bin/Aion.Host/debug/Aion.Host.dll'
            if os.environ.get('AION_ARTIFACTS_PATH') else
            ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll')


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet'), 'Aion.Host is not built')
class DotnetFileLogTests(unittest.TestCase):
    def run_host(self, **settings):
        port = free_port()
        env = dict(os.environ, Aion__LocalPort=str(port), Logging__LogLevel__Default='Debug', **settings)
        for key in ('ConnectionStrings__Aion', 'ConnectionStrings__Auth'):
            env.pop(key, None)
        with tempfile.TemporaryFile() as log:
            return self._run(env, port, log)

    def _run(self, env, port, log):
        process = subprocess.Popen(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                try:
                    socket.create_connection(('127.0.0.1', port), timeout=0.5).close()
                    break
                except OSError:
                    self.assertIsNone(process.poll(), 'host exited during startup')
                    time.sleep(0.2)
            else:
                self.fail('host did not open the account port')
            time.sleep(0.5)
        finally:
            process.terminate()
            process.wait(timeout=10)
        log.seek(0)
        return log.read().decode('utf-8', 'replace')

    def test_off_by_default(self):
        with tempfile.TemporaryDirectory() as temp:
            self.run_host(Aion__Logging__File__Directory=temp)
            self.assertEqual(os.listdir(temp), [])

    def test_writes_and_rotates(self):
        with tempfile.TemporaryDirectory() as temp:
            output = self.run_host(Aion__Logging__File__Enabled='true', Aion__Logging__File__Directory=temp,
                                   Aion__Logging__File__MinLevel='Debug', Aion__Logging__File__MaxFileBytes='65536',
                                   Aion__Logging__File__MaxFiles='3')
            self.assertIn('Writing log files to', output)
            self.assertTrue((Path(temp) / 'server.log').exists())
            text = ''.join(p.read_text(encoding='utf-8') for p in Path(temp).glob('server*.log'))
            self.assertIn('Writing log files to', text)
            self.assertLessEqual(len(list(Path(temp).glob('server*.log'))), 3)

    def test_unusable_directory_does_not_stop_startup(self):
        with tempfile.TemporaryDirectory() as temp:
            blocker = Path(temp) / 'not-a-directory'
            blocker.write_text('x', encoding='utf-8')
            output = self.run_host(Aion__Logging__File__Enabled='true', Aion__Logging__File__Directory=str(blocker / 'logs'))
            self.assertIn('Log files are off', output)
        output = self.run_host(Aion__Logging__File__Enabled='true', Aion__Logging__File__Directory=str(HOST_DLL.parent / 'logs'))
        self.assertIn('inside the host folder', output)
        self.assertFalse((HOST_DLL.parent / 'logs').exists())
        output = self.run_host(Aion__Logging__File__Enabled='true', Aion__Logging__File__Directory='relative/logs')
        self.assertIn('must be an absolute path', output)


if __name__ == '__main__':
    unittest.main()
