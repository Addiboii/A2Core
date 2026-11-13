#!/usr/bin/env bash
# One-time migration download for the Windows setup (run with sudo on the Linux server):
#   sudo tools/windows_migration_token.sh
# makes /var/lib/aion/a2core-migrate/<token>/ with a fresh database dump, the roles (same password hashes), the settings
# (aion.env), the TLS certificate and key, the database credentials and a manifest; the host serves them at
# /a2core/migrate/<token>/<file> for 24 hours (Aion:Web:MigrationDir). Prints the Windows command. Old tokens are removed.
# The live server keeps running: stop it first (systemctl stop aion-host) for a final move, so nothing changes after the dump.
set -euo pipefail
DIR=${AION_MIGRATION_DIR:-/var/lib/aion/a2core-migrate}
BIN=${PG_BIN:-/usr/lib/postgresql/18/bin}
PORT=${PGPORT:-55432}
SERVER=${AION_SETUP_SERVER:-$(. /etc/aion/aion.env; echo "${Aion__Network__PublicAddress:-127.0.0.1}")}
install -d -o aion -g aion -m 700 "$DIR"
find "$DIR" -mindepth 1 -maxdepth 1 -type d -mmin +1440 -exec rm -rf {} +
token=$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 40)
out="$DIR/$token"
install -d -o aion -g aion -m 700 "$out"
export PGPASSWORD=$(python3 -c "import json;print(json.load(open('/var/lib/aion/postgres/credentials.json'))['aion_admin'])")
"$BIN/pg_dumpall" -h 127.0.0.1 -p "$PORT" -U aion_admin --roles-only > "$out/roles.sql"
"$BIN/pg_dump" -h 127.0.0.1 -p "$PORT" -U aion_admin -Fc -f "$out/aion.dump" aion
cp /etc/aion/aion.env "$out/aion.env"
cp /etc/aion/tls/cert.pem "$out/cert.pem"; cp /etc/aion/tls/key.pem "$out/key.pem"
cp /var/lib/aion/postgres/credentials.json "$out/credentials.json"
python3 - "$out" <<'PY'
import json, os, sys, datetime
out = sys.argv[1]
json.dump({'created': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
           'files': {f: os.path.getsize(os.path.join(out, f)) for f in sorted(os.listdir(out))}}, open(os.path.join(out, 'manifest.json'), 'w'), indent=1)
PY
chown -R aion:aion "$out"; chmod 600 "$out"/*
echo "Migration download ready for 24 hours."
echo "On Windows, in an administrator PowerShell:"
echo "  \$env:A2CORE_MIGRATE_FROM='$SERVER'; \$env:A2CORE_TOKEN='$token'; irm http://$SERVER:8091/a2core/server-setup.txt | iex"
