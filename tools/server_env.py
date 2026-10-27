"""Write the environment file for a hosted server (systemd EnvironmentFile; deploy/linux).

Reads the database credentials made by `postgres_dev.py setup` (AION_PG_HOME) and the
server data directory unpacked from `pack_server_data.py`, and prints KEY=VALUE lines:
database connections, bind/public addresses, HTTPS, the native ports, chat, the platform
bootstrap and the NCGuard sink. Contains passwords: write it to a file only root and the
service account can read.
"""
import argparse
import ipaddress
import json
import secrets
import string
from pathlib import Path
import sys

try:
    from .postgres_dev import HOME, PORT
except ImportError:
    from postgres_dev import HOME, PORT

PORTS = {'account': 8780, 'bootstrap': 8087, 'login': 3700, 'game': 13328, 'chat': 8091, 'ncguard': 443}


def connection(role, password, pool):
    # Generated passwords are URL-safe, so the value needs no quoting (EnvironmentFile is not a shell).
    return (f'Host=127.0.0.1;Port={PORT};Database=aion;Username={role};Password={password};'
            f'Maximum Pool Size={pool};Timeout=5;Command Timeout=15')


def geolocation(text):
    """Our GET geolocations/v1.0/me body (same layout as run_game_chain.geolocation_response)."""
    lat, lon, country = text.split(',')
    lat, lon, country = float(lat), float(lon), country.strip().upper()
    if not (-90 <= lat <= 90 and -180 <= lon <= 180 and len(country) == 2 and country.isalpha()):
        raise ValueError('--geolocation must be LAT,LON,CC')
    return {'geolocation': country, 'coordinates': {'latitude': f'{lat:.4f}', 'longitude': f'{lon:.4f}'}}


def environment(args, credentials):
    data = args.data.resolve()
    required = {'bootstrap keys': data / 'bootstrap/keys/app-key.json',
                'policy': data / 'bootstrap/policy/app-policy.json',
                'NCGuard fixture': data / 'ncguard/fixture.json'}
    missing = [f'{name} ({path})' for name, path in required.items() if not path.exists()]
    if missing:
        raise ValueError('Server data incomplete: ' + ', '.join(missing))
    env = {
        'ASPNETCORE_ENVIRONMENT': 'Production',
        'ConnectionStrings__Aion': connection('aion_app', credentials['aion_app'], 32),
        'ConnectionStrings__Auth': connection('aion_auth', credentials['aion_auth'], 16),
        'Aion__LocalPort': PORTS['account'],
        # Closed by default: admins create accounts on the dashboard.
        'Aion__Accounts__Registration': 'true' if getattr(args, 'open_registration', False) else 'false',
        'Aion__Network__BindAddress': args.bind,
        'Aion__Network__PublicAddress': args.public_address,
        'Aion__Native__LoginPort': PORTS['login'],
        'Aion__Native__GamePort': PORTS['game'],
        'Aion__Native__Logins': 'accounts',
        'Aion__Native__MaxConnections': args.max_connections,
        'Aion__ChatPort': PORTS['chat'],
        'Aion__Platform__Port': PORTS['bootstrap'],
        'Aion__Platform__FixtureDirectory': data / 'bootstrap/keys',
        'Aion__Platform__PolicyFile': data / 'bootstrap/policy/app-policy.json',
        # Policy custom_data switches of the game (its key table, read from the client 2026-10-18). The title screen's
        # Security Settings button is governed by SecondaryAuthCheckType (value CANDIDATE until a launch confirms).
        # SteamShowOnlySteamLogin=true is NOT set: it moves the client to the Steam token login route
        # (sessions/v1.0/login_sessions;token); the bootstrap answers it with the login session object, which the client
        # rejected on 2026-10-18 (200, then "failed to login"), so the reply shape is still unknown.
        'Aion__Platform__CustomData__SecondaryAuthCheckType': 'None',
        'Aion__NcGuard__Port': PORTS['ncguard'],
        'Aion__NcGuard__FixtureFile': data / 'ncguard/fixture.json',
        'Aion__NcGuard__DataDirectory': args.state / 'ncguard',
        # Players' anti-cheat requests are answered, never recorded.
        'Aion__NcGuard__Capture': 'false',
    }
    if (data / 'terrain').is_dir():
        env['Aion__Native__TerrainDirectory'] = data / 'terrain'
    if (data / 'navmesh').is_dir():
        env['Aion__Native__NavMeshDirectory'] = data / 'navmesh'
    # Player downloads (the launcher exe), served at /download/.
    env['Aion__Web__Downloads'] = args.state / 'downloads'
    # Dashboard icons and 3D preview (staff-only on a public host), when the export was unpacked.
    if (data / 'wardrobe/wardrobe.json').exists():
        env['Aion__WardrobePath'] = data / 'wardrobe'
    # The game's world-map paintings for the dashboard's live map (tools/MapImageExport: <id>.webp and maps.json).
    if (data / 'map-images/maps.json').exists():
        env['Aion__MapImagesPath'] = data / 'map-images'
    # Dashboard backups (Server page): pg_dump through postgres_dev.py into the state folder update.sh uses too.
    env['Aion__Backups__Directory'] = args.state / 'backups'
    env['Aion__Backups__Tool'] = Path(__file__).resolve().parent / 'postgres_dev.py'
    env['Aion__Backups__PgHome'] = args.state / 'postgres'
    if args.geolocation != 'off':
        env['Aion__Platform__GeolocationFile'] = args.state / 'geolocation-response.json'
    if args.proxy:
        env['Aion__Https__Proxy'] = 'true'
    else:
        env['Aion__Https__CertificateFile'] = args.certificate.resolve()
        env['Aion__Https__KeyFile'] = args.key.resolve()
    # The staff dashboard's private address; kept across updates (install.sh passes the current one).
    env['Aion__Web__StaffPath'] = getattr(args, 'staff_path', None) or '/' + ''.join(secrets.choice(string.ascii_lowercase + string.digits) for _ in range(16))
    for index, map_id in enumerate(args.preload):
        env[f'Aion__Native__PreloadMaps__{index}'] = map_id
    return env


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--public-address', required=True, help='IPv4 address players connect to')
    parser.add_argument('--bind', default='0.0.0.0', help='listen address (default every interface)')
    parser.add_argument('--data', type=Path, required=True, help='unpacked server data (pack_server_data.py)')
    parser.add_argument('--state', type=Path, required=True, help='writable service directory (NCGuard state, geolocation)')
    tls = parser.add_mutually_exclusive_group(required=True)
    tls.add_argument('--proxy', action='store_true', help='a TLS proxy on this machine serves the account site')
    tls.add_argument('--certificate', type=Path, help='PEM certificate for the account port (with --key)')
    parser.add_argument('--key', type=Path, help='PEM private key for --certificate')
    parser.add_argument('--open-registration', action='store_true', help='let anyone create an account on the account page')
    parser.add_argument('--staff-path', help='staff dashboard address, e.g. /k3x9... (default: a new random one)')
    parser.add_argument('--max-connections', type=int, default=200, help='per native port')
    parser.add_argument('--preload', type=int, nargs='*', default=[1000], help='maps loaded at startup')
    parser.add_argument('--geolocation', default='59.9139,10.7522,NO', metavar='LAT,LON,CC',
                        help="server location for the region screen ('off' to omit)")
    args = parser.parse_args(argv)
    try:
        address = ipaddress.ip_address(args.public_address)
    except ValueError:
        parser.error('--public-address must be an IPv4 address')
    if address.version != 4 or address.is_loopback:
        parser.error('--public-address must be a non-loopback IPv4 address')
    if args.certificate and not args.key:
        parser.error('--certificate needs --key')
    if args.staff_path is not None and (len(args.staff_path) < 9 or not args.staff_path.startswith('/')
                                        or not all(c.isalnum() or c in '-_' for c in args.staff_path[1:])):
        parser.error('--staff-path must be / followed by at least 8 letters, digits, - or _')
    credentials = json.loads((HOME / 'credentials.json').read_text(encoding='utf-8'))
    try:
        env = environment(args, credentials)
        if args.geolocation != 'off':
            args.state.mkdir(parents=True, exist_ok=True)
            (args.state / 'geolocation-response.json').write_text(json.dumps(geolocation(args.geolocation)), encoding='utf-8')
    except ValueError as error:
        parser.error(str(error))
    sys.stdout.write(''.join(f'{key}={value}\n' for key, value in env.items()))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
