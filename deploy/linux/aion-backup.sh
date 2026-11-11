#!/usr/bin/env bash
# Nightly database backup (aion-backup.timer -> aion-backup.service, as the aion user): pg_dump through
# tools/postgres_dev.py into /var/lib/aion/backups/nightly-<time>.dump, then nightly dumps older than
# AION_BACKUP_KEEP_DAYS days (OURS default 14; set it in /etc/aion/local.env) are removed. Only nightly-*.dump
# files age out here: before-update, before-restore and dashboard backups are left alone. Restore: aion-restore.
set -euo pipefail
DIR="${AION_BACKUP_DIR:-/var/lib/aion/backups}"
KEEP_DAYS="${AION_BACKUP_KEEP_DAYS:-14}"
[[ "$KEEP_DAYS" =~ ^[0-9]+$ && "$KEEP_DAYS" -ge 1 ]] || { echo "AION_BACKUP_KEEP_DAYS must be a whole number of days, 1 or more (it is '$KEEP_DAYS')" >&2; exit 2; }
export AION_PG_HOME="${AION_PG_HOME:-/var/lib/aion/postgres}"
[[ -n "${AION_PG_BIN:-}" ]] || AION_PG_BIN="$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1)"
export AION_PG_BIN PYTHONDONTWRITEBYTECODE=1
TOOLS="${AION_TOOLS:-/opt/aion/src/tools}"
PYTHON="${AION_PYTHON:-python3}"

NAME="nightly-$(date +%Y%m%d-%H%M%S).dump"
PARTIAL="$DIR/.$NAME.partial"
trap 'rm -f -- "$PARTIAL"' EXIT
# Written under a temporary name first: a failed dump never looks like a backup, and nothing is pruned after one.
"$PYTHON" "$TOOLS/postgres_dev.py" backup "$PARTIAL"
mv -- "$PARTIAL" "$DIR/$NAME"
echo "Database backed up to $DIR/$NAME ($(du -h "$DIR/$NAME" | cut -f1))"
# Age in whole days (find -mtime): a nightly dump KEEP_DAYS days old or older goes; the one just made always stays.
find "$DIR" -maxdepth 1 -type f -name 'nightly-*.dump' ! -name "$NAME" -mtime +"$((KEEP_DAYS - 1))" -print -delete \
  | sed 's/^/Removed old backup /'
