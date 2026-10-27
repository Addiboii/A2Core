"""Reversible Windows saved-config experiment; not a configuration/game server."""
import argparse
import csv
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import quote, urlsplit
import uuid

ROOT = Path(__file__).resolve().parents[1]
SECTION = '/Script/Aion2.AionGamePlatformSettings'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def config_path():
    return Path(os.environ['LOCALAPPDATA']) / 'AION2/Saved_Steam/Config/Windows/GamePlatform.ini'


def decode_process_listing(raw):
    """Accept UTF-8, UTF-16, and legacy tasklist bytes without failing the restore."""
    if not raw:
        return ''
    if raw.startswith((b'\xff\xfe', b'\xfe\xff')):
        return raw.decode('utf-16', errors='replace')
    if b'\x00' in raw[:64]:
        if len(raw) % 2:
            raw = raw[:-1]
        return raw.decode('utf-16-le', errors='replace')
    return raw.decode('utf-8', errors='replace')


def process_image_present(raw, image_name):
    text = decode_process_listing(raw)
    wanted = image_name.lower()
    return any(row and row[0].lower() == wanted for row in csv.reader(text.splitlines()))


def game_running():
    result = subprocess.run(['tasklist.exe', '/FI', 'IMAGENAME eq AION2.exe', '/FO', 'CSV', '/NH'],
                            check=True, capture_output=True)
    return process_image_present(result.stdout, 'AION2.exe')


def candidate(original, port):
    encoding = 'utf-16' if original[:2] in (b'\xff\xfe', b'\xfe\xff') else 'utf-8-sig'
    text = original.decode(encoding)
    if any(line.strip().lower() == '[' + SECTION.lower() + ']' for line in text.splitlines()):
        raise ValueError('Target settings section already exists; review it before testing.')
    # The updater profile (DefaultGamePlatform NcUpdaterConfigType=GLOBAL_LIVE ->
    # mobile.ncupdate.com) is replaced by a loopback one, so the dev client does not
    # contact NCSoft's update service (the firewall rules block what remains).
    addition = ('\r\n[' + SECTION + ']\r\n'
                'NcPlatformNetworks=(("LIVE",(ConfigurationServerUrl="http://127.0.0.1:'
                + str(port) + '")))\r\n'
                'NcUpdaterConfigType=LOCAL\r\n'
                'NcUpdaterConfigs=(("LOCAL",(ServerUrl="127.0.0.1",GameId_Windows="A2_WW_L_GA_PURPLE")))\r\n')
    return (text + addition).encode(encoding)


def handler(events, key_response=None, policy_response=None, record_route_paths=False,
            maintenance_response=None, session_response=None, session_identifier=None,
            authn_response=None, secondary_start_response=None, secondary_finish_response=None,
            authn_factory=None, geolocation_response=None):
    class ProbeHandler(BaseHTTPRequestHandler):
        def setup(self):
            super().setup()
            self.connection.settimeout(2)

        def log_message(self, *_):
            pass

        def respond(self):
            # Drain small fixed-length bodies without retaining them, so Windows
            # does not reset the connection before the failure response is read.
            try:
                remaining = int(self.headers.get('Content-Length', '0'))
            except ValueError:
                remaining = -1
            if not 0 <= remaining <= 1048576 or self.headers.get('Transfer-Encoding'):
                self.close_connection = True
                return
            parts = urlsplit(self.path)
            path = parts.path
            # Our own Lime chat routes (ChatServerURL) keep headers and body (capped) so
            # the chat API can be implemented; other routes still drop bodies unread.
            keep = record_route_paths and (path == '/local-lime' or path.startswith('/local-lime/'))
            kept = bytearray()
            while remaining:
                chunk = self.rfile.read(min(remaining, 8192))
                if not chunk:
                    break
                remaining -= len(chunk)
                if keep and len(kept) < 65536:
                    kept += chunk[:65536 - len(kept)]
            known =('/MobileAppConfigSdk/app/key', '/MobileAppConfigSdk/app/policy',
                     '/local-api-gate', '/local-login-web', '/local-login-api')
            maintenance_path = '/MobileAppConfigSdk/app/policy/maintenance'
            route_family = next((prefix for prefix in known[2:]
                                 if path == prefix or path.startswith(prefix + '/')), None)
            # Optional research captures retain paths; for the config SDK (also the Lime
            # chat SDK's ConfigUrl) the query and non-secret caller headers too. Never bodies.
            events.append({'utc': datetime.now(timezone.utc).isoformat(),
                           'method': self.command, 'path': path if path in known else '<other>'})
            if route_family:
                events[-1]['route_family'] = route_family
            if record_route_paths:
                events[-1]['route_path'] = path
                if path.startswith('/MobileAppConfigSdk'):
                    events[-1]['query'] = parts.query
                    events[-1]['header_names'] = sorted({name.lower() for name in self.headers.keys()})
                    events[-1]['caller_headers'] = {
                        name.lower(): value for name, value in self.headers.items()
                        if name.lower() == 'user-agent' or name.lower().startswith('lime-')}
                if keep:
                    events[-1]['query'] = parts.query
                    events[-1]['headers'] = {name: value for name, value in self.headers.items()}
                    events[-1]['body'] = kept.decode('utf-8', 'replace')
            body = b'{"local_probe":true,"ready":false}'
            status = 503
            if key_response is not None and self.command == 'GET' and path == known[0]:
                body = key_response
                status = 200
            if policy_response is not None and self.command == 'GET' and path == known[1]:
                body = policy_response
                status = 200
            if maintenance_response is not None and self.command == 'POST' and path == maintenance_path:
                body = maintenance_response
                status = 200
            if authn_response is not None and self.command == 'POST' and path == '/local-api-gate/sessions/v1.0/authn_token':
                body = authn_response
                status = 200
            if authn_factory is not None and self.command == 'POST' and path == '/local-api-gate/sessions/v1.0/authn_token':
                try:
                    body = json.dumps(authn_factory()).encode('utf-8')
                    status = 200
                except Exception:
                    body = b'{"error":"Launch authorization rejected"}'
                    status = 401
            if secondary_start_response is not None and self.command == 'POST' and path == '/local-api-gate/secondary_auth/v1.0/my/auth;start':
                body = secondary_start_response
                status = 200
            if secondary_finish_response is not None and self.command == 'POST' and path == '/local-api-gate/secondary_auth/v1.0/my/auth;finish':
                body = secondary_finish_response
                status = 200
            if geolocation_response is not None and self.command == 'GET' and path == '/local-api-gate/geolocations/v1.0/me':
                body = geolocation_response
                status = 200
            issued_session_path = ('/local-api-gate/sessions/v1.0/' + quote(session_identifier, safe='')
                                   if session_identifier else None)
            if session_response is not None and self.command == 'GET' and (
                    path == issued_session_path or re.fullmatch(
                    r'/local-api-gate/sessions/v1\.0/[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', path)):
                body = session_response
                status = 200
            events[-1]['response_status'] = status
            self.send_response(status)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Connection', 'close')
            self.end_headers()
            if self.command != 'HEAD':
                self.wfile.write(body)
            self.close_connection = True

        do_GET = do_POST = do_PUT = do_HEAD = do_OPTIONS = respond
    return ProbeHandler


def save_state(directory, state):
    (directory / 'state.json').write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')


def restore(directory, target):
    state = json.loads((directory / 'state.json').read_text(encoding='utf-8'))
    if Path(state['target']).resolve() != target.resolve():
        raise ValueError('Backup target differs from the requested saved config.')
    original = (directory / 'original.ini').read_bytes()
    if digest(original) != state['original_sha256']:
        raise ValueError('Original backup hash mismatch; restoration stopped.')
    if state['status'] == 'restored':
        return
    if target.exists():
        current = target.read_bytes()
        if current != original:
            # Preserve any writes made by the game during this test.
            (directory / ('before-restore-' + uuid.uuid4().hex + '.ini')).write_bytes(current)
    target.write_bytes(original)
    state['status'] = 'restored'
    save_state(directory, state)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8087)
    parser.add_argument('--seconds', type=int, default=180)
    parser.add_argument('--restore', type=Path, help='Restore a previous local-bootstrap report directory')
    args = parser.parse_args()
    if os.name != 'nt':
        parser.error('Windows is required for the process guard and Steam saved-config path.')
    if not 1024 <= args.port <= 65535 or not 10 <= args.seconds <= 600:
        parser.error('Use port 1024..65535 and duration 10..600 seconds.')
    if game_running():
        parser.error('Close AION2 first. No config was changed.')
    target = config_path()
    local = ROOT / 'research/local'
    if args.restore:
        directory = args.restore.resolve()
        if directory.parent != local.resolve() or not directory.name.startswith('local-bootstrap-'):
            parser.error('Restore directory must be a local-bootstrap report under research/local.')
        restore(directory, target)
        print('Original config restored.')
        return
    for previous in local.glob('local-bootstrap-*/state.json'):
        if json.loads(previous.read_text(encoding='utf-8'))['status'] != 'restored':
            parser.error('Restore the previous test first: --restore ' + str(previous.parent))
    original = target.read_bytes()  # Require the observed existing Steam config.
    modified = candidate(original, args.port)
    events = []
    # Bind before editing so a port collision cannot leave a configuration change.
    with HTTPServer(('127.0.0.1', args.port), handler(events)) as server:
        server.timeout = 1
        directory = local / ('local-bootstrap-' + datetime.now().strftime('%Y%m%d-%H%M%S-') + uuid.uuid4().hex[:8])
        directory.mkdir(parents=True)
        (directory / 'original.ini').write_bytes(original)
        state = {'target': str(target), 'original_sha256': digest(original),
                 'candidate_sha256': digest(modified), 'status': 'prepared'}
        save_state(directory, state)
        try:
            target.write_bytes(modified)
            state['status'] = 'active'
            save_state(directory, state)
            print('Config backed up. Launch AION2 normally now. This listener returns HTTP 503.', flush=True)
            print('Report/recovery directory: ' + str(directory), flush=True)
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline:
                server.handle_request()
        except KeyboardInterrupt:
            pass
        finally:
            (directory / 'requests.json').write_text(json.dumps(events, indent=2) + '\n', encoding='utf-8')
            print('Recorded requests: ' + str(len(events)), flush=True)
            if game_running():
                print('Close AION2 to finish restoring its configuration.', flush=True)
                try:
                    while game_running():
                        time.sleep(1)
                except KeyboardInterrupt:
                    print('Restore still pending. Close AION2, then run:', flush=True)
                    print('python tools/probe_local_bootstrap.py --restore "' + str(directory) + '"', flush=True)
                    return
            restore(directory, target)
            print('Original config restored. No request is proof only that this test saw none.', flush=True)


if __name__ == '__main__':
    main()
