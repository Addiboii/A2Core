"""Local PostgreSQL lifecycle, checked migrations, catalogue import and backups.

Uses vendor psql/pg_ctl tools, not a per-request game-server database driver.
Credentials and all generated data stay under ignored research/local/postgres.

Linux servers (deploy/linux/README.md): the same project-owned cluster, with the
distribution's PostgreSQL binaries (AION_PG_BIN, else the newest
/usr/lib/postgresql/<n>/bin) and AION_PG_HOME for the cluster and credentials.
"""
import argparse
import csv
import glob
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ['AION_PG_HOME']) if os.environ.get('AION_PG_HOME') else ROOT / 'research/local/postgres'
PORT = 55432
WINDOWS = os.name == 'nt'


def binaries():
    """Windows: the project's extracted binaries. Elsewhere: the distribution's server binaries."""
    if os.environ.get('AION_PG_BIN'):
        return Path(os.environ['AION_PG_BIN'])
    if WINDOWS:
        return HOME / 'pgsql/bin'
    versions = sorted(glob.glob('/usr/lib/postgresql/*/bin'), key=lambda p: int(Path(p).parent.name) if Path(p).parent.name.isdigit() else 0)
    return Path(versions[-1]) if versions else Path('/usr/bin')


BIN = binaries()


def literal(text):
    return "'" + str(text).replace("'", "''") + "'"


def run(binary, args=(), *, text=None, role='aion_admin', database='aion', check=True):
    env = os.environ.copy()
    if (HOME / 'credentials.json').exists():
        credentials = json.loads((HOME / 'credentials.json').read_text())
        env.update(PGHOST='127.0.0.1', PGPORT=str(PORT), PGUSER=role,
                   PGPASSWORD=credentials[role], PGDATABASE=database, PGCONNECT_TIMEOUT='5')
    command = [str(BIN / (binary + ('.exe' if WINDOWS else ''))), *map(str, args)]
    flags = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
    if binary == 'pg_ctl':
        # A detached Windows postgres may inherit pipes and keep communicate()
        # waiting after pg_ctl exits. Use a file and DEVNULL for lifecycle calls.
        with (HOME / 'lifecycle.log').open('w+', encoding='utf-8') as log:
            process = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=log,
                                     stderr=subprocess.STDOUT, env=env,
                                     creationflags=flags, timeout=45)
            log.seek(0)
            result = subprocess.CompletedProcess(command, process.returncode, '', log.read())
    else:
        result = subprocess.run(command, input=text, encoding='utf-8', capture_output=True,
                                env=env, creationflags=flags, timeout=180)
    if check and result.returncode:
        # SQL errors can contain input; redact generated credentials before printing.
        message = result.stderr
        if (HOME / 'credentials.json').exists():
            for password in json.loads((HOME / 'credentials.json').read_text()).values():
                message = message.replace(password, '[redacted]')
        raise RuntimeError(f'{binary} failed: {message}')
    return result


def sql(text, *, role='aion_admin', database='aion', check=True):
    return run('psql', ['-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1'], text=text,
               role=role, database=database, check=check)


def setup():
    HOME.mkdir(parents=True, exist_ok=True)
    archive = HOME / 'postgresql-18.6-1-windows-x64-binaries.zip'
    if not (BIN / ('initdb' + ('.exe' if WINDOWS else ''))).exists() and not WINDOWS:
        raise ValueError(f'No PostgreSQL server binaries in {BIN}; install postgresql or set AION_PG_BIN')
    if not BIN.exists():
        with zipfile.ZipFile(archive) as bundle:
            for member in bundle.infolist():
                relative = Path(member.filename)
                if len(relative.parts) < 2 or relative.parts[:2] not in (
                        ('pgsql', 'bin'), ('pgsql', 'lib'), ('pgsql', 'share')):
                    continue
                destination = (HOME / relative).resolve()
                if not destination.is_relative_to(HOME.resolve()):
                    raise ValueError('Unsafe archive path')
                bundle.extract(member, HOME)
        with archive.open('rb') as downloaded:
            archive_digest = hashlib.file_digest(downloaded, 'sha256').hexdigest()
        (HOME / 'binary-source.json').write_text(json.dumps({
            'url': 'https://get.enterprisedb.com/postgresql/' + archive.name,
            'sha256': archive_digest
        }, indent=2), encoding='utf-8')
    credentials_path = HOME / 'credentials.json'
    if not credentials_path.exists():
        if (HOME / 'data').exists():
            raise ValueError('Existing cluster has no credentials file; refusing to replace credentials')
        credentials_path.write_text(json.dumps({name: secrets.token_urlsafe(36)
            for name in ('aion_admin', 'aion_app', 'aion_auth')}), encoding='utf-8')
    credentials = json.loads(credentials_path.read_text())
    if 'aion_auth' not in credentials:
        credentials['aion_auth'] = secrets.token_urlsafe(36)
        credentials_path.write_text(json.dumps(credentials), encoding='utf-8')
    if not (HOME / 'data/PG_VERSION').exists():
        password_file = HOME / 'init-password.txt'
        password_file.write_text(credentials['aion_admin'], encoding='ascii')
        try:
            run('initdb', ['-D', HOME / 'data', '-U', 'aion_admin', '--encoding=UTF8',
                          '--locale=C', '--auth=scram-sha-256', '--pwfile', password_file])
        finally:
            password_file.unlink(missing_ok=True)
        with (HOME / 'data/postgresql.conf').open('a', encoding='utf-8') as config:
            # TCP on loopback only; no Unix socket (the distribution's socket directory belongs to its own cluster).
            config.write("\n# Project-owned cluster: loopback only\nlisten_addresses='127.0.0.1'\n"
                         f"port={PORT}\npassword_encryption='scram-sha-256'\nunix_socket_directories=''\n")
    start()
    sql("SELECT 'CREATE ROLE aion_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_owner')\\gexec\n"
        "SELECT 'CREATE ROLE aion_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_app')\\gexec\n"
        "SELECT 'CREATE ROLE aion_auth LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_auth')\\gexec\n"
        'ALTER ROLE aion_app PASSWORD ' + literal(credentials['aion_app']) + ';\n'
        'ALTER ROLE aion_auth PASSWORD ' + literal(credentials['aion_auth']) + ';\n'
        "SELECT 'CREATE DATABASE aion OWNER aion_owner' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname='aion')\\gexec\n",
        database='postgres')
    sql('REVOKE ALL ON DATABASE aion FROM PUBLIC; GRANT CONNECT ON DATABASE aion TO aion_app,aion_auth;')
    migrate()


def start():
    # Windows sandbox process inspection can report a running server as stopped.
    # Prefer an authenticated check and verify this is our own cluster first.
    probe = sql("SELECT current_setting('data_directory');", database='postgres', check=False)
    if probe.returncode == 0:
        if os.path.normcase(str(Path(probe.stdout.strip()).resolve())) != os.path.normcase(str((HOME / 'data').resolve())):
            raise ValueError('Port is serving a different PostgreSQL data directory')
        return
    status = run('pg_ctl', ['status', '-D', HOME / 'data'], check=False)
    if status.returncode:
        run('pg_ctl', ['start', '-D', HOME / 'data', '-l', HOME / 'server.log', '-w', '-t', '30'])


def migration_script(files):
    out = ["BEGIN; SELECT pg_advisory_xact_lock(731924001); SET LOCAL ROLE aion_owner;",
           'CREATE TABLE IF NOT EXISTS public.schema_migrations(version text PRIMARY KEY, sha256 text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());']
    for path in files:
        body = path.read_text(encoding='utf-8')
        version, digest = literal(path.name), literal(hashlib.sha256(path.read_bytes()).hexdigest())
        out.extend([f"DO $check$ BEGIN IF EXISTS(SELECT FROM public.schema_migrations WHERE version={version} AND sha256<>{digest}) THEN RAISE EXCEPTION 'Applied migration changed'; END IF; END $check$;",
                    f'SELECT NOT EXISTS(SELECT FROM public.schema_migrations WHERE version={version}) AS apply \\gset',
                    '\\if :apply', body,
                    f'INSERT INTO public.schema_migrations(version,sha256) VALUES({version},{digest});', '\\endif'])
    return '\n'.join(out + ['COMMIT;'])


def migrate():
    sql(migration_script(sorted((ROOT / 'database/migrations').glob('*.sql'))))


def import_catalog(directory):
    directory = Path(directory)
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    canonical = json.dumps(manifest, sort_keys=True, separators=(',', ':'))
    build_id = hashlib.sha256(canonical.encode()).hexdigest()
    with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', newline='', suffix='.sql',
                                     dir=HOME, delete=False) as stream:
        path = Path(stream.name)
        stream.write('BEGIN; SELECT pg_advisory_xact_lock(731924002); SET LOCAL ROLE aion_owner;\n')
        stream.write('INSERT INTO catalog.builds(build_id,manifest) VALUES(' + literal(build_id) + ',' + literal(canonical) + ') ON CONFLICT DO NOTHING;\n')
        stream.write('CREATE TEMP TABLE incoming(document jsonb);\nCOPY incoming FROM STDIN WITH (FORMAT csv);\n')
        writer = csv.writer(stream, lineterminator='\n')
        counts = {'items': 0, 'skills': 0}
        with (directory / 'entries.jsonl').open(encoding='utf-8') as source:
            for line in source:
                document = json.loads(line)
                counts[document['kind']] += 1
                writer.writerow([json.dumps(document, ensure_ascii=False)])
        if counts != manifest['counts']:
            raise ValueError('Export counts differ from manifest')
        stream.write('\\.\n')
        for kind in ('items', 'skills'):
            # Immutable snapshots: the same build/ID may be reimported only unchanged.
            stream.write(f"DO $verify$ BEGIN IF EXISTS(SELECT FROM incoming i JOIN catalog.{kind} c ON c.build_id={literal(build_id)} AND c.template_id=(i.document->>'id')::integer WHERE i.document->>'kind'='{kind}' AND c.definition<>i.document) THEN RAISE EXCEPTION 'Catalogue snapshot changed'; END IF; END $verify$;\n")
            stream.write(f"INSERT INTO catalog.{kind}(build_id,template_id,name,category,definition) SELECT {literal(build_id)},(document->>'id')::integer,document->>'name',document->>'category',document FROM incoming WHERE document->>'kind'='{kind}' ON CONFLICT DO NOTHING;\n")
        stream.write('COMMIT;\n')
    try:
        run('psql', ['-X', '-q', '-v', 'ON_ERROR_STOP=1', '-f', path])
    finally:
        path.unlink(missing_ok=True)
    return {'build_id': build_id, 'counts': counts}


def verify_backup(path):
    """Restore into a uniquely named disposable DB; never restore over aion."""
    name = 'aion_restore_' + secrets.token_hex(8)
    sql(f'CREATE DATABASE {name};', database='postgres')
    try:
        run('pg_restore', ['--no-owner', '--no-privileges', '--exit-on-error',
                           '--dbname', name, Path(path).resolve()])
        return sql("SELECT 'items='||count(*) FROM catalog.items;"
                   "SELECT 'skills='||count(*) FROM catalog.skills;"
                   "SELECT 'migrations='||count(*) FROM public.schema_migrations;", database=name).stdout.strip()
    finally:
        # Name consists only of this fixed prefix and freshly generated hex.
        sql(f'DROP DATABASE {name};', database='postgres')


ROLES = ('gamemaster', 'admin')


def role_statement(username, role, revoke=False):
    """Operator-only staff role change for a local account; returns the SQL to run."""
    if role not in ROLES or not username or len(username) > 32 or not all(c.isalnum() or c == '_' for c in username):
        raise ValueError('Use a local username and a role of gamemaster or admin')
    account = f"SELECT account_id FROM auth.accounts WHERE provider='local' AND subject={literal(username)}"
    if revoke:
        return f"DELETE FROM auth.account_roles WHERE role={literal(role)} AND account_id=({account}) RETURNING account_id;"
    return (f"INSERT INTO auth.account_roles SELECT account_id,{literal(role)} FROM auth.accounts "
            f"WHERE provider='local' AND subject={literal(username)} ON CONFLICT DO NOTHING RETURNING account_id;")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    for command in ('setup', 'start', 'stop', 'status', 'migrate'):
        sub.add_parser(command)
    load = sub.add_parser('import-catalog')
    load.add_argument('--directory', type=Path, default=ROOT / 'research/local/game-catalog-v2')
    backup = sub.add_parser('backup')
    backup.add_argument('output', type=Path)
    verify = sub.add_parser('verify-backup')
    verify.add_argument('path', type=Path)
    staff = sub.add_parser('role', help='grant or revoke a gamemaster/admin role (operator only)')
    staff.add_argument('username')
    staff.add_argument('role', choices=ROLES)
    staff.add_argument('--revoke', action='store_true')
    args = parser.parse_args()
    if args.command == 'setup':
        setup()
        print('PostgreSQL configured at 127.0.0.1:55432, database aion; migrations applied.')
    elif args.command == 'start':
        start()
    elif args.command == 'stop':
        run('pg_ctl', ['stop', '-D', HOME / 'data', '-m', 'fast', '-w'])
    elif args.command == 'status':
        print(sql("SELECT version(); SELECT 'items='||count(*) FROM catalog.items; SELECT 'skills='||count(*) FROM catalog.skills;").stdout)
    elif args.command == 'migrate':
        migrate()
    elif args.command == 'import-catalog':
        print(json.dumps(import_catalog(args.directory), indent=2))
    elif args.command == 'backup':
        if args.output.exists():
            raise ValueError('Backup destination already exists')
        args.output.parent.mkdir(parents=True, exist_ok=True)
        run('pg_dump', ['-Fc', '-f', args.output])
        print('Backup saved:', args.output)
    elif args.command == 'verify-backup':
        print(verify_backup(args.path))
    elif args.command == 'role':
        changed = sql(role_statement(args.username, args.role, args.revoke)).stdout.strip()
        exists = sql(f"SELECT count(*) FROM auth.accounts WHERE provider='local' AND subject={literal(args.username)};").stdout.strip()
        if exists == '0':
            raise SystemExit(f'No local account named {args.username}.')
        action = 'Revoked' if args.revoke else 'Granted'
        print(f'{action} {args.role} for {args.username}.' if changed else f'No change: {args.username} already {"lacks" if args.revoke else "has"} {args.role}.')


if __name__ == '__main__':
    main()
