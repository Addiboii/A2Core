"""Reversible Saved_AionDev-only localhost routing test with cached launch args.

Defaults to 503; optional candidate key/policy fixtures test settings responses.
"""
import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import subprocess
import time
import uuid
from http.server import HTTPServer

try:
    from .probe_local_bootstrap import candidate, digest, handler, restore, game_running
    from .probe_host_startup import ROOT, prepare_launch, backup, collect_logs, save
    from .audit_saved_data import snapshot, compare
except ImportError:
    from probe_local_bootstrap import candidate, digest, handler, restore, game_running
    from probe_host_startup import ROOT, prepare_launch, backup, collect_logs, save
    from audit_saved_data import snapshot, compare


def development_config(local_appdata):
    return Path(local_appdata) / 'AION2/Saved_AionDev/Config/Windows/GamePlatform.ini'


def recovery_directory(path):
    directory = Path(path).resolve()
    if directory.parent != (ROOT / 'research/local').resolve() or not directory.name.startswith('dev-bootstrap-'):
        raise ValueError('Recovery must name a dev-bootstrap run directly under research/local')
    return directory


def final_audits(directory, roots):
    for label, root in roots.items():
        before = json.loads((directory / (label + '-before.json')).read_text(encoding='utf-8'))
        after = snapshot(root)
        after['comparison'] = compare(before, after)
        save(directory / (label + '-after-restoration.json'), after)


class ExternalServer:
    """Stand-in when another process (the .NET host's Aion:Platform port) answers the
    bootstrap requests: the launcher only edits, launches, waits and restores."""
    timeout = 1

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def handle_request(self):
        time.sleep(1)


def port_listening(port):
    import socket
    try:
        socket.create_connection(('127.0.0.1', port), timeout=1).close()
        return True
    except OSError:
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8087)
    parser.add_argument('--seconds', type=int, default=150)
    parser.add_argument('--restore', type=Path)
    parser.add_argument('--key-response', type=Path, help='Candidate app/key JSON')
    parser.add_argument('--policy-response', type=Path, help='Candidate encrypted app/policy JSON')
    parser.add_argument('--maintenance-response', type=Path, help='Candidate encrypted maintenance JSON')
    parser.add_argument('--session-response', type=Path, help='Synthetic candidate for GET sessions/v1.0/{UUID}')
    parser.add_argument('--authn-response', type=Path, help='Synthetic authn_token response for the exact token POST')
    parser.add_argument('--launch-authorization', type=Path, help='Explicit account-page launch authorization; overrides synthetic auth token')
    parser.add_argument('--geolocation-response', type=Path,
                        help='Our own JSON for GET geolocations/v1.0/me (region distance; candidate shape)')
    parser.add_argument('--secondary-start-response', type=Path, help='Synthetic session_id candidate for the exact secondary-auth start POST')
    parser.add_argument('--secondary-finish-response', type=Path, help='Synthetic token candidate for the exact secondary-auth finish POST')
    parser.add_argument('--record-route-paths', action='store_true',
                        help='Retain HTTP paths under research/local; for /MobileAppConfigSdk also the query, '
                             'header names and User-Agent/Lime-* values. Never bodies or other headers')
    parser.add_argument('--external-server', action='store_true',
                        help='Another process already serves the port (server/Aion.Host Aion:Platform); '
                             'only edit, launch, wait and restore. Response options are not used')
    args = parser.parse_args()
    if args.external_server and any(getattr(args, name) for name in (
            'key_response', 'policy_response', 'maintenance_response', 'session_response', 'authn_response',
            'launch_authorization', 'geolocation_response', 'secondary_start_response', 'secondary_finish_response')):
        parser.error('--external-server takes its responses from the serving process')
    if os.name != 'nt' or not 1024 <= args.port <= 65535 or not 10 <= args.seconds <= 600:
        parser.error('Windows required; port 1024..65535 and duration 10..600')
    if game_running():
        parser.error('Close AION2 first; no configuration changed')
    local = ROOT / 'research/local'
    appdata = Path(os.environ['LOCALAPPDATA'])
    target = development_config(appdata)
    roots = {'aion': appdata / 'AION2', 'nccr': appdata / 'NCSOFT/NccrData/com.ncsoft.aion2global'}
    if args.restore:
        directory = recovery_directory(args.restore)
        restore(directory, target)
        final_audits(directory, roots)
        print('Development configuration restored and audited.')
        return
    for pattern in ('dev-bootstrap-*/state.json', 'local-bootstrap-*/state.json'):
        for previous in local.glob(pattern):
            if json.loads(previous.read_text(encoding='utf-8'))['status'] != 'restored':
                parser.error('A prior override is pending: ' + str(previous.parent))
    if args.external_server and not port_listening(args.port):
        parser.error('Nothing is listening on 127.0.0.1:%d; start the host first' % args.port)
    executable, arguments, image_sha = prepare_launch()
    original = target.read_bytes()
    modified = candidate(original, args.port)
    events = []
    key_response = None
    if args.key_response:
        import base64
        fixture = json.loads(args.key_response.read_text(encoding='utf-8'))
        if set(fixture) != {'value'} or not isinstance(fixture['value'], str):
            parser.error('Key fixture must contain only a string value')
        if len(base64.b64decode(fixture['value'], validate=True)) != 256:
            parser.error('Expected a 256-byte RSA ciphertext')
        key_response = json.dumps(fixture).encode('utf-8')
    policy_response = None
    if args.policy_response:
        if key_response is None:
            parser.error('Policy response requires a matching key response')
        fixture = json.loads(args.policy_response.read_text(encoding='utf-8'))
        if set(fixture) != {'value'} or not isinstance(fixture['value'], str):
            parser.error('Policy fixture must contain only a string value')
        ciphertext = base64.b64decode(fixture['value'], validate=True)
        if not ciphertext or len(ciphertext) % 16 or len(ciphertext) > 1048576:
            parser.error('Policy ciphertext must contain 1..65536 AES blocks')
        policy_response = json.dumps(fixture).encode('utf-8')
    maintenance_response = None
    if args.maintenance_response:
        if policy_response is None:
            parser.error('Maintenance response requires matching key and policy responses')
        fixture = json.loads(args.maintenance_response.read_text(encoding='utf-8'))
        if set(fixture) != {'value'} or not isinstance(fixture['value'], str):
            parser.error('Maintenance fixture must contain only a string value')
        ciphertext = base64.b64decode(fixture['value'], validate=True)
        if not ciphertext or len(ciphertext) % 16 or len(ciphertext) > 1048576:
            parser.error('Maintenance ciphertext must contain 1..65536 AES blocks')
        maintenance_response = json.dumps(fixture).encode('utf-8')
    session_response = None
    session_identifier = None
    if args.session_response:
        if maintenance_response is None:
            parser.error('Session response requires key, policy and maintenance responses')
        fixture = json.loads(args.session_response.read_text(encoding='utf-8'))
        fields = {'session', 'session_secret', 'game_account_id', 'login_name', 'user_id', 'user_name'}
        if not isinstance(fixture, dict) or set(fixture) != fields or any(
                not isinstance(value, str) or not 1 <= len(value) <= 512 for value in fixture.values()):
            parser.error('Expected six nonempty candidate session string fields, each at most 512 characters')
        session_response = json.dumps(fixture).encode('utf-8')
        session_identifier = fixture['session']
    authn_response = None
    if args.authn_response:
        if session_response is None:
            parser.error('Token response requires a session response')
        fixture = json.loads(args.authn_response.read_text(encoding='utf-8'))
        if not isinstance(fixture, dict) or set(fixture) != {'authn_token'} or not isinstance(fixture['authn_token'], str) or not 1 <= len(fixture['authn_token']) <= 4096:
            parser.error('Token fixture must contain only a nonempty authn_token string up to 4096 characters')
        authn_response = json.dumps(fixture).encode('utf-8')
    secondary_start_response = None
    if args.secondary_start_response:
        if authn_response is None:
            parser.error('Secondary-auth start response requires a token response')
        fixture = json.loads(args.secondary_start_response.read_text(encoding='utf-8'))
        if not isinstance(fixture, dict) or set(fixture) != {'session_id'} or not isinstance(fixture['session_id'], str) or not 1 <= len(fixture['session_id']) <= 512:
            parser.error('Secondary-auth fixture must contain only a nonempty session_id string up to 512 characters')
        secondary_start_response = json.dumps(fixture).encode('utf-8')
    secondary_finish_response = None
    if args.secondary_finish_response:
        if secondary_start_response is None:
            parser.error('Secondary-auth finish response requires a start response')
        fixture = json.loads(args.secondary_finish_response.read_text(encoding='utf-8'))
        # A token field is observed; this string format is a bounded test candidate.
        if not isinstance(fixture, dict) or set(fixture) != {'token'} or not isinstance(fixture['token'], str) or not 1 <= len(fixture['token']) <= 4096:
            parser.error('Secondary-auth finish fixture must contain only a nonempty token string up to 4096 characters')
        secondary_finish_response = json.dumps(fixture).encode('utf-8')
    authn_factory = None
    if args.launch_authorization:
        try:
            from .client_bridge import LaunchTokenProvider
        except ImportError:
            from client_bridge import LaunchTokenProvider
        authn_factory = LaunchTokenProvider(args.launch_authorization)
    serving = ExternalServer() if args.external_server else HTTPServer(
        ('127.0.0.1', args.port), handler(events, key_response, policy_response,
                                          args.record_route_paths, maintenance_response, session_response,
                                          session_identifier, authn_response, secondary_start_response,
                                          secondary_finish_response, authn_factory=authn_factory,
                                          geolocation_response=args.geolocation_response.read_bytes()
                                          if args.geolocation_response else None))
    with serving as server:
        server.timeout = 1
        directory = local / ('dev-bootstrap-' + datetime.now().strftime('%Y%m%d-%H%M%S-') + uuid.uuid4().hex[:8])
        directory.mkdir()
        for label, root in roots.items():
            save(directory / (label + '-before.json'), backup(root, directory / 'backups' / label))
        (directory / 'original.ini').write_bytes(original)
        state = {'target': str(target), 'original_sha256': digest(original),
                 'candidate_sha256': digest(modified), 'executable_sha256': image_sha,
                 'status': 'prepared', 'port': args.port,
                 'key_response_sha256': digest(key_response) if key_response else None,
                 'policy_response_sha256': digest(policy_response) if policy_response else None,
                 'record_route_paths': args.record_route_paths,
                 'external_server': args.external_server,
                 'maintenance_response_sha256': digest(maintenance_response) if maintenance_response else None,
                 'session_response_sha256': digest(session_response) if session_response else None,
                 'authn_response_sha256': digest(authn_response) if authn_response else None,
                 'secondary_start_response_sha256': digest(secondary_start_response) if secondary_start_response else None,
                 'secondary_finish_response_sha256': digest(secondary_finish_response) if secondary_finish_response else None,
                 'limitations': ['Only supplied candidate responses return 200; other routes return 503.',
                                'Listener does not independently attribute requests to a process.',
                                'Saved_AionDev is not complete account/service isolation.']}
        save(directory / 'state.json', state)
        started = time.time()
        try:
            # Recheck before writing after the potentially lengthy backup step.
            if game_running() or target.read_bytes() != original:
                state['status'] = 'restored'
                save(directory / 'state.json', state)
                raise ValueError('Process/config changed during preparation; launch cancelled')
            target.write_bytes(modified)
            state['status'] = 'active'
            save(directory / 'state.json', state)
            process = subprocess.Popen([str(executable), *arguments], cwd=executable.parent,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            state['initial_pid'] = process.pid
            save(directory / 'state.json', state)
            served = 'external server' if args.external_server else (
                'key ' + ('candidate 200' if key_response else '503') + '; policy ' + ('candidate 200' if policy_response else '503'))
            print('Development client launched; ' + served + '. Run: ' + str(directory.relative_to(ROOT)), flush=True)
            deadline = time.monotonic() + args.seconds
            next_logs = time.monotonic()
            seen_game = False
            while time.monotonic() < deadline:
                server.handle_request()
                save(directory / 'requests.json', events)
                if time.monotonic() >= next_logs:
                    save(directory / 'collection-errors.json', collect_logs(directory, started))
                    next_logs = time.monotonic() + 5
                    # End early once the game has run and closed (or crashed): nothing left to serve.
                    if game_running():
                        seen_game = True
                    elif seen_game:
                        print('Development client closed.', flush=True)
                        break
        finally:
            save(directory / 'requests.json', events)
            save(directory / 'collection-errors.json', collect_logs(directory, started))
            print('Requests recorded: ' + str(len(events)), flush=True)
            if state['status'] != 'restored':
                if game_running():
                    print('Close development client to restore its configuration.', flush=True)
                while game_running():
                    time.sleep(1)
                restore(directory, target)
            final_audits(directory, roots)
            print('Development config restored. Final audits saved.', flush=True)


if __name__ == '__main__':
    main()
