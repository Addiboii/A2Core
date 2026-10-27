"""Run the local account UI with app/auth credentials supplied only via child env."""
import argparse
import json
import os
import subprocess
import secrets
try:
    from .postgres_dev import ROOT, HOME, PORT
except ImportError:
    from postgres_dev import ROOT, HOME, PORT


def connection_string(database, role, password):
    # Generated passwords are URL-safe; also quote values for connection syntax.
    quote = lambda value: '"' + str(value).replace('"', '""') + '"'
    return (f'Host=127.0.0.1;Port={PORT};Database={quote(database)};Username={quote(role)};'
            f'Password={quote(password)};Maximum Pool Size=16;Timeout=5;Command Timeout=15')


def host_environment(credentials, database='aion', port=8780):
    env = os.environ.copy()
    env['ConnectionStrings__Aion'] = connection_string(database, 'aion_app', credentials['aion_app'])
    env['ConnectionStrings__Auth'] = connection_string(database, 'aion_auth', credentials['aion_auth'])
    env['Aion__LocalPort'] = str(port)
    env['Aion__BridgeSecret'] = bridge_secret()
    # Development-only: lets the staff dashboard preview items with the local export.
    if (ROOT / 'research/local/wardrobe/wardrobe.json').exists():
        env['Aion__WardrobePath'] = str(ROOT / 'research/local/wardrobe')
    return env


def bridge_secret():
    path = HOME / 'client-bridge-secret.txt'
    try:
        with path.open('x', encoding='ascii') as stream:
            stream.write(secrets.token_hex(32))
    except FileExistsError:
        pass
    secret = path.read_text(encoding='ascii').strip()
    if len(secret) != 64 or any(c not in '0123456789abcdef' for c in secret):
        raise ValueError('Invalid local client bridge configuration')
    return secret


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8780)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error('Choose an unprivileged TCP port')
    credentials = json.loads((HOME / 'credentials.json').read_text())
    assembly = ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll'
    print(f'Account page: http://127.0.0.1:{args.port}', flush=True)
    child = subprocess.Popen(['dotnet', str(assembly)], cwd=ROOT / 'server/Aion.Host',
                             env=host_environment(credentials, port=args.port),
                             creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    try:
        return child.wait()
    except KeyboardInterrupt:
        return 0
    finally:
        if child.poll() is None:
            child.terminate()
            child.wait(timeout=10)


if __name__ == '__main__':
    raise SystemExit(main())
