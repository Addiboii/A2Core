#!/usr/bin/env bash
# Installs the Stage 0 backups (run with sudo): WAL archiving in the aion cluster (restarts Postgres and the host), the
# backup script, the nightly timer; then takes the first backup.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
ROOT=/var/lib/aion/backups
install -d -o aion -g aion -m 750 "$ROOT" "$ROOT/base" "$ROOT/dump" "$ROOT/wal"
install -d /usr/local/lib/aion
install -m 755 "$here/aion-backup.sh" "$here/aion-restore-test.sh" /usr/local/lib/aion/
install -m 644 "$here/aion-backup.service" "$here/aion-backup.timer" /etc/systemd/system/
PW=$(python3 -c "import json;print(json.load(open('/var/lib/aion/postgres/credentials.json'))['aion_admin'])")
PGPASSWORD="$PW" psql -h 127.0.0.1 -p 55432 -U aion_admin -d aion -v ON_ERROR_STOP=1 \
  -c "ALTER SYSTEM SET archive_mode = 'on'" \
  -c "ALTER SYSTEM SET archive_command = 'test ! -f $ROOT/wal/%f && cp %p $ROOT/wal/%f'" \
  -c "ALTER SYSTEM SET archive_timeout = '300'"
systemctl restart aion-postgres
systemctl restart aion-host
systemctl daemon-reload
systemctl enable --now aion-backup.timer
sudo -u aion /usr/local/lib/aion/aion-backup.sh
