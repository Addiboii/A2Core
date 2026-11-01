"""Helpers for start-all.cmd on Windows (also runnable on Linux for smoke checks)."""
from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT / 'tools') not in sys.path:
    sys.path.insert(0, str(ROOT / 'tools'))

from postgres_dev import HOME, PORT  # noqa: E402
from run_account_server import host_environment  # noqa: E402
from run_game_chain import (  # noqa: E402
    BOOTSTRAP_PORT,
    FIXTURES,
    KEY_FIXTURES,
    geolocation_response,
)

RUNTIME = ROOT / '.start-all' / 'runtime'
SESSION = ROOT / '.start-all' / 'session.json'
HOST_DLL = ROOT / 'server' / 'Aion.Host' / 'bin' / 'Debug' / 'net10.0' / 'Aion.Host.dll'
DEFAULT_POLICY = FIXTURES / 'policy-login-route-20261009-1230'
CHAT_PORT = 8091
ACCOUNT_PORT = 8780
NATIVE_HTTP_PORT = 8781


def _msg(text: str) -> None:
    print(text, flush=True)


def check_dotnet() -> int:
    try:
        result = subprocess.run(['dotnet', '--version'], capture_output=True, text=True, check=False, timeout=30)
    except FileNotFoundError:
        _msg('[ERROR] .NET SDK not found. Install .NET 10 SDK: https://dotnet.microsoft.com/download')
        return 1
    version = (result.stdout or result.stderr or '').strip().splitlines()[0] if result.returncode == 0 else ''
    if not version.startswith('10.'):
        _msg(f'[ERROR] .NET SDK 10.x required, found: {version or "unknown"}')
        return 1
    return 0


def check_python() -> int:
    if sys.version_info < (3, 11):
        _msg(f'[ERROR] Python 3.11+ required, found: {sys.version.split()[0]}')
        return 1
    return 0


def check_postgres_files() -> int:
    archive = HOME / 'postgresql-18.6-1-windows-x64-binaries.zip'
    initdb = HOME / 'pgsql' / 'bin' / 'initdb.exe'
    if os.name == 'nt' and not initdb.exists() and not archive.exists():
        _msg('[ERROR] No dev PostgreSQL: put the zip into')
        _msg(f'  {archive}')
        _msg('  or install PostgreSQL and set AION_PG_BIN.')
        return 1
    return 0


def check_fixtures() -> int:
    policy = DEFAULT_POLICY / 'app-policy.json'
    keys = KEY_FIXTURES / 'app-key.json'
    missing = []
    if not policy.is_file():
        missing.append(str(policy))
    if not keys.is_file():
        missing.append(str(keys))
    if missing:
        _msg('[ERROR] Platform bootstrap fixtures (8087) are missing:')
        for path in missing:
            _msg(f'  {path}')
        _msg('  Provide research/local/bootstrap-fixtures yourself or build them with pack_server_data.')
        return 1
    return 0


def find_ncguard_fixture() -> Path | None:
    explicit = os.environ.get('AION_NCGUARD_FIXTURE')
    if explicit:
        path = Path(explicit)
        return path if path.is_file() else None
    candidates = sorted(ROOT.glob('research/local/**/fixture.json'))
    for path in candidates:
        if 'ncguard' in str(path).lower():
            return path
    return None


def postgres_up() -> int:
    import postgres_dev

    try:
        if not (HOME / 'credentials.json').exists():
            postgres_dev.setup()
        else:
            postgres_dev.start()
            postgres_dev.migrate()
    except Exception as error:
        _msg(f'[ERROR] PostgreSQL: {error}')
        return 1
    _msg(f'PostgreSQL ready: 127.0.0.1:{PORT}, database aion.')
    return 0


def write_env_file(path: Path, env: dict[str, str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = []
    for key, value in env.items():
        if any(ch in value for ch in ('\r', '\n', '\0')):
            raise ValueError(f'Unsafe env value for {key}')
        lines.append(f'{key}={value}')
    path.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def _playtest_debug_env() -> dict[str, str]:
    if os.environ.get('AION_PLAYTEST_DEBUG', '').lower() not in ('1', 'true', 'yes'):
        return {}
    root = ROOT.resolve()
    logs = root / 'research' / 'local' / 'logs'
    return {
        'Aion__Debug__Enabled': 'true',
        'Aion__Debug__LogDirectory': str(logs),
        'Aion__World__MapEventAskNpcProximityFallback': 'true',
    }


def native_host_env(runtime_dir: Path) -> dict[str, str]:
    credentials = json.loads((HOME / 'credentials.json').read_text(encoding='utf-8'))
    runtime_dir.mkdir(parents=True, exist_ok=True)
    env = host_environment(credentials, port=NATIVE_HTTP_PORT)
    env['ASPNETCORE_ENVIRONMENT'] = 'Development'
    env.update({
        'Aion__Native__LoginPort': '3700',
        'Aion__Native__GamePort': '13328',
        'Aion__Native__Logins': 'accounts',
        'Aion__Native__IntroCharacter': 'false',
        'Aion__Native__CharacterLevel': '1',
        'Aion__ChatPort': str(CHAT_PORT),
        'Aion__Native__TraceDirectory': str(runtime_dir.resolve()),
        'Aion__Platform__Port': str(BOOTSTRAP_PORT),
        'Aion__Platform__FixtureDirectory': str(KEY_FIXTURES.resolve()),
        'Aion__Platform__PolicyFile': str((DEFAULT_POLICY / 'app-policy.json').resolve()),
        'Aion__Platform__RequestLog': str((runtime_dir / 'bootstrap-requests.jsonl').resolve()),
    })
    env.pop('Aion__ChatDevelopmentLogins', None)
    geolocation = runtime_dir / 'geolocation-response.json'
    geolocation.write_text(json.dumps(geolocation_response('59.9139,10.7522,NO')), encoding='utf-8')
    env['Aion__Platform__GeolocationFile'] = str(geolocation.resolve())
    env.update(_playtest_debug_env())
    return env


def ncguard_host_env(runtime_dir: Path, fixture: Path) -> dict[str, str]:
    runtime_dir.mkdir(parents=True, exist_ok=True)
    stop_file = runtime_dir / 'ncguard-stop'
    return {
        'ASPNETCORE_ENVIRONMENT': 'Development',
        'Aion__LocalPort': '8782',
        'Aion__NcGuard__Port': '443',
        'Aion__NcGuard__FixtureFile': str(fixture.resolve()),
        'Aion__NcGuard__DataDirectory': str((runtime_dir / 'ncguard-state').resolve()),
        'Aion__NcGuard__Capture': 'false',
        'Aion__NcGuard__RewriteHosts': 'true',
        'Aion__NcGuard__StopFile': str(stop_file.resolve()),
    }


def wait_ports(ports: list[int], timeout: float) -> int:
    deadline = time.monotonic() + timeout
    pending = set(ports)
    while pending and time.monotonic() < deadline:
        ready = set()
        for port in pending:
            with socket.socket() as probe:
                probe.settimeout(0.5)
                try:
                    probe.connect(('127.0.0.1', port))
                    ready.add(port)
                except OSError:
                    pass
        pending -= ready
        if pending:
            time.sleep(0.4)
    if pending:
        _msg('[ERROR] Timed out waiting for ports: ' + ', '.join(str(p) for p in sorted(pending)))
        return 1
    return 0


def write_launch_scripts(runtime_dir: Path) -> bool:
    """Write .cmd helpers under runtime_dir; return True if NCGuard env was written."""
    runtime_dir.mkdir(parents=True, exist_ok=True)
    native_env = native_host_env(runtime_dir)
    write_env_file(runtime_dir / 'native-host.env', native_env)
    host_dir = HOST_DLL.parent
    native_cmd = runtime_dir / 'start-native.cmd'
    native_cmd.write_text(
        '@echo off\r\n'
        'chcp 65001>nul\r\n'
        f'cd /d "{host_dir}"\r\n'
        'for /f "usebackq tokens=1* delims==" %%A in ("%~dp0native-host.env") do set "%%A=%%B"\r\n'
        'echo [A2Core] Login 3700, Game 13328, Bootstrap 8087, Chat 8091 (accounts).\r\n'
        'dotnet Aion.Host.dll\r\n',
        encoding='utf-8',
    )
    ncguard = find_ncguard_fixture()
    if ncguard:
        write_env_file(runtime_dir / 'ncguard-host.env', ncguard_host_env(runtime_dir, ncguard))
        nc_cmd = runtime_dir / 'start-ncguard.cmd'
        nc_cmd.write_text(
            '@echo off\r\n'
            'chcp 65001>nul\r\n'
            f'cd /d "{ROOT}"\r\n'
            'echo [A2Core] Starting NCGuard TLS on 443 (UAC required)...\r\n'
            f'python tools\\start_all_windows.py launch-ncguard "{runtime_dir.as_posix()}"\r\n'
            'if errorlevel 1 echo NCGuard did not start.\r\n'
            'pause\r\n',
            encoding='utf-8',
        )
    return ncguard is not None


def launch_ncguard(runtime_dir: Path) -> int:
    fixture = find_ncguard_fixture()
    if not fixture:
        _msg('[WARNING] NCGuard fixture not found.')
        return 2
    if not HOST_DLL.is_file():
        _msg('[ERROR] Build Aion.Host first (dotnet build).')
        return 1
    runtime_dir = runtime_dir.resolve()
    env = ncguard_host_env(runtime_dir, fixture)
    try:
        from elevated_dotnet_host import start_elevated_host
    except ImportError:
        from tools.elevated_dotnet_host import start_elevated_host
    try:
        start_elevated_host(HOST_DLL, env, runtime_dir / 'ncguard-elevated')
    except RuntimeError as error:
        _msg(f'[ERROR] NCGuard: {error}')
        return 1
    if wait_ports([443], 60):
        return 1
    _msg('NCGuard is listening on 127.0.0.1:443 (elevated). Log: .start-all/runtime/ncguard-elevated/host.log')
    return 0


def write_session(*, ncguard: bool) -> None:
    SESSION.parent.mkdir(parents=True, exist_ok=True)
    SESSION.write_text(json.dumps({
        'account_port': ACCOUNT_PORT,
        'native_http_port': NATIVE_HTTP_PORT,
        'login_port': 3700,
        'game_port': 13328,
        'bootstrap_port': BOOTSTRAP_PORT,
        'chat_port': CHAT_PORT,
        'ncguard_port': 443 if ncguard else None,
    }, indent=2), encoding='utf-8')


def load_session() -> dict:
    if not SESSION.is_file():
        return {}
    return json.loads(SESSION.read_text(encoding='utf-8'))


def stop_listeners(session: dict) -> None:
    ports = [session.get('account_port'), session.get('native_http_port'),
             session.get('login_port'), session.get('game_port'),
             session.get('bootstrap_port'), session.get('chat_port')]
    if session.get('ncguard_port'):
        ports.append(session['ncguard_port'])
    for port in {p for p in ports if isinstance(p, int)}:
        _kill_port_listeners(port)
    stop_file = RUNTIME / 'ncguard-stop'
    if stop_file.parent.exists():
        stop_file.touch(exist_ok=True)


def _kill_port_listeners(port: int) -> None:
    if os.name != 'nt':
        return
    script = (
        f"$p={port}; "
        "Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue | "
        "Select-Object -ExpandProperty OwningProcess -Unique | "
        "ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(['powershell', '-NoProfile', '-Command', script], check=False)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    sub.add_parser('check')
    sub.add_parser('postgres-up')
    write_native = sub.add_parser('write-native-env')
    write_native.add_argument('runtime_dir', type=Path, nargs='?', default=RUNTIME)
    write_nc = sub.add_parser('write-ncguard-env')
    write_nc.add_argument('runtime_dir', type=Path, nargs='?', default=RUNTIME)
    wait = sub.add_parser('wait-ports')
    wait.add_argument('ports', type=int, nargs='+')
    wait.add_argument('--timeout', type=float, default=120.0)
    session = sub.add_parser('write-session')
    session.add_argument('--ncguard', action='store_true')
    sub.add_parser('prepare-launch')
    launch_nc = sub.add_parser('launch-ncguard')
    launch_nc.add_argument('runtime_dir', type=Path, nargs='?', default=RUNTIME)
    sub.add_parser('stop-servers')
    args = parser.parse_args(argv)

    if args.command == 'check':
        code = check_python() or check_dotnet() or check_postgres_files() or check_fixtures()
        return code
    if args.command == 'postgres-up':
        return postgres_up()
    if args.command == 'write-native-env':
        write_env_file(args.runtime_dir / 'native-host.env', native_host_env(args.runtime_dir))
        fixture = find_ncguard_fixture()
        if fixture:
            write_env_file(args.runtime_dir / 'ncguard-host.env', ncguard_host_env(args.runtime_dir, fixture))
            _msg(str(fixture))
        return 0
    if args.command == 'write-ncguard-env':
        fixture = find_ncguard_fixture()
        if not fixture:
            _msg('[WARNING] NCGuard fixture not found; the NCGuard window will not be started.')
            return 2
        write_env_file(args.runtime_dir / 'ncguard-host.env', ncguard_host_env(args.runtime_dir, fixture))
        _msg(str(fixture))
        return 0
    if args.command == 'wait-ports':
        return wait_ports(args.ports, args.timeout)
    if args.command == 'write-session':
        write_session(ncguard=args.ncguard)
        return 0
    if args.command == 'prepare-launch':
        has_nc = write_launch_scripts(RUNTIME)
        write_session(ncguard=has_nc)
        _msg(str(RUNTIME))
        return 0
    if args.command == 'launch-ncguard':
        return launch_ncguard(args.runtime_dir)
    if args.command == 'stop-servers':
        stop_listeners(load_session())
        try:
            import postgres_dev
            if (HOME / 'data' / 'PG_VERSION').exists():
                postgres_dev.run('pg_ctl', ['stop', '-D', HOME / 'data', '-m', 'fast', '-w'], check=False)
        except Exception:
            pass
        if SESSION.exists():
            SESSION.unlink()
        _msg('A2Core services stopped (dev PostgreSQL too, if the script started it).')
        return 0
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
