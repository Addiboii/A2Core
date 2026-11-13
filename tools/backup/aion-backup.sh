#!/usr/bin/env bash
# A2Core nightly database backup (Stage 0, core-upgrade.md). Runs as the aion user (aion-backup.timer).
#  - base/<UTC stamp>/: pg_basebackup (tar, compressed) = the starting point of a point-in-time restore; the WAL files that
#    follow it are archived continuously by Postgres itself (archive_command -> /var/lib/aion/backups/wal).
#  - dump/aion-<UTC stamp>.dump: pg_dump custom format (a logical copy: restores on any platform, e.g. the Windows VM).
#  - Keeps KEEP_DAYS days (default 14): older base backups and dumps are removed, and the archived WAL older than the oldest
#    kept base backup (pg_archivecleanup).
# Copy off the VM: the Windows side pulls /var/lib/aion/backups/dump/ with scp.
set -euo pipefail
ROOT=${AION_BACKUP_ROOT:-/var/lib/aion/backups}
BIN=${PG_BIN:-/usr/lib/postgresql/18/bin}
KEEP_DAYS=${KEEP_DAYS:-14}
PORT=${PGPORT:-55432}
CRED=${AION_PG_CREDENTIALS:-/var/lib/aion/postgres/credentials.json}
export PGPASSWORD=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['aion_admin'])" "$CRED")
stamp=$(date -u +%Y%m%d-%H%M%S)
mkdir -p "$ROOT/base" "$ROOT/dump" "$ROOT/wal"
"$BIN/pg_basebackup" -h 127.0.0.1 -p "$PORT" -U aion_admin -D "$ROOT/base/$stamp" -Ft -z -X none -c fast --label="aion-$stamp"
"$BIN/pg_dump" -h 127.0.0.1 -p "$PORT" -U aion_admin -Fc -f "$ROOT/dump/aion-$stamp.dump" aion
# Retention.
find "$ROOT/base" -mindepth 1 -maxdepth 1 -type d -mtime +"$KEEP_DAYS" -exec rm -rf {} +
find "$ROOT/dump" -name 'aion-*.dump' -mtime +"$KEEP_DAYS" -delete
oldest=$(ls -1 "$ROOT/base" | sort | head -1)
# The first WAL file the oldest base backup needs is named in its backup label (inside base.tar.gz).
label_wal=$(tar -xOzf "$ROOT/base/$oldest/base.tar.gz" backup_label 2>/dev/null | sed -n 's/^START WAL LOCATION: .* (file \([0-9A-F]*\)).*/\1/p')
if [ -n "$label_wal" ]; then "$BIN/pg_archivecleanup" "$ROOT/wal" "$label_wal"; fi
echo "backup $stamp done: $(du -sh "$ROOT/base/$stamp" | cut -f1) base, $(du -sh "$ROOT/dump/aion-$stamp.dump" | cut -f1) dump, $(ls "$ROOT/wal" | wc -l) WAL files kept"
