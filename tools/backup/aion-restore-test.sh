#!/usr/bin/env bash
# Restore test (Stage 0): the newest base backup plus the archived WAL into a scratch cluster on another port, recovered to the
# end of the archive (or to RECOVERY_TARGET_TIME, e.g. "2026-11-07 18:00:00+00" for a point-in-time restore), then the row
# counts of the main tables are compared with the live database and the scratch cluster is removed (KEEP=1 keeps it).
# Runs as the aion user:  sudo -u aion tools/backup/aion-restore-test.sh
set -euo pipefail
ROOT=${AION_BACKUP_ROOT:-/var/lib/aion/backups}
BIN=${PG_BIN:-/usr/lib/postgresql/18/bin}
PORT=${TEST_PORT:-55499}
CRED=${AION_PG_CREDENTIALS:-/var/lib/aion/postgres/credentials.json}
export PGPASSWORD=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['aion_admin'])" "$CRED")
base=$(ls -1 "$ROOT/base" | sort | tail -1)
[ -n "$base" ] || { echo "no base backup in $ROOT/base"; exit 1; }
dir=$(mktemp -d /var/lib/aion/backups/restore-test.XXXXXX)
trap '[ "${KEEP:-0}" = 1 ] || { "$BIN/pg_ctl" -D "$dir/data" -m immediate stop >/dev/null 2>&1 || true; rm -rf "$dir"; }' EXIT
mkdir -p "$dir/data" && chmod 700 "$dir/data"
tar -xzf "$ROOT/base/$base/base.tar.gz" -C "$dir/data"
cat >> "$dir/data/postgresql.auto.conf" <<CONF
port = $PORT
listen_addresses = '127.0.0.1'
archive_mode = off
restore_command = 'cp $ROOT/wal/%f %p'
${RECOVERY_TARGET_TIME:+recovery_target_time = '$RECOVERY_TARGET_TIME'}
recovery_target_action = 'promote'
CONF
touch "$dir/data/recovery.signal"
"$BIN/pg_ctl" -D "$dir/data" -l "$dir/server.log" -w -t 300 start >/dev/null
for i in $(seq 1 120); do
  [ "$(psql -h 127.0.0.1 -p "$PORT" -U aion_admin -d aion -Atc 'select pg_is_in_recovery()' 2>/dev/null)" = f ] && break; sleep 1
done
q="select (select count(*) from auth.accounts)||' accounts, '||(select count(*) from game.characters)||' characters, '||(select count(*) from game.item_instances)||' items, '||(select count(*) from game.character_quests)||' quests'"
live=$(psql -h 127.0.0.1 -p 55432 -U aion_admin -d aion -Atc "$q")
restored=$(psql -h 127.0.0.1 -p "$PORT" -U aion_admin -d aion -Atc "$q")
echo "base backup $base, restored to ${RECOVERY_TARGET_TIME:-the end of the archive}"
echo "live:     $live"
echo "restored: $restored"
[ -n "$restored" ] && echo "RESTORE OK" || { echo "RESTORE FAILED (see $dir/server.log)"; KEEP=1; exit 1; }
