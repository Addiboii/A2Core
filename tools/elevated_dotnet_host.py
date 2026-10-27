"""Server-only UAC launch; the calling launcher and game stay unelevated."""
import ctypes
import json
import os
from pathlib import Path
import subprocess
import time


def is_elevated():
    return os.name == 'nt' and bool(ctypes.windll.shell32.IsUserAnAdmin())


class ElevatedHost:
    def __init__(self, directory):
        self.directory = Path(directory)

    def poll(self):
        result = self.directory / 'dotnet-exit.json'
        if not result.exists():
            return None
        return json.loads(result.read_text(encoding='utf-8-sig'))['exit_code']

    def wait(self, timeout=None):
        deadline = None if timeout is None else time.monotonic() + timeout
        while True:
            code = self.poll()
            if code is not None:
                return code
            if deadline is not None and time.monotonic() >= deadline:
                raise subprocess.TimeoutExpired('elevated .NET host', timeout)
            time.sleep(0.1)

    def terminate(self):
        # Never force-kill: the server must restore its hosts-file block.
        (self.directory / 'dotnet-stop').touch()


def start_elevated_host(dll, environment, directory):
    directory = Path(directory).resolve()
    config = directory / 'dotnet-start.json'
    helper = Path(__file__).with_name('run_elevated_dotnet_host.ps1').resolve()
    settings = {k: v for k, v in environment.items()
                if k.startswith(('Aion__', 'ConnectionStrings__'))}
    config.write_text(json.dumps({'dll': str(Path(dll).resolve()), 'environment': settings}),
                      encoding='utf-8')
    # Pass only a configuration path through UAC, never database credentials.
    def quote(value):
        return "'" + str(value).replace("'", "''") + "'"
    arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + str(helper) + '" -Config "' + str(config) + '"'
    command = ("$ErrorActionPreference='Stop'; try { Start-Process powershell.exe "
               "-Verb RunAs -WindowStyle Hidden -ArgumentList " + quote(arguments) +
               " | Out-Null } catch { exit 1 }")
    try:
        result = subprocess.run(['powershell.exe', '-NoProfile', '-Command', command],
                                capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError('Server elevation failed or was cancelled; no client launched.')
    except BaseException:
        config.unlink(missing_ok=True)
        raise
    return ElevatedHost(directory)
