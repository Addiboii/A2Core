#!/usr/bin/env bash
# Replaces the whole database (accounts, characters, items) with a backup from /var/lib/aion/backups (or a path).
# Asks you to type the backup's file name, checks that the backup restores (into a scratch database), stops the
# host, saves the current database as before-restore-<time>.dump, restores in one transaction (a failure changes
# nothing) and starts the host again.
#   sudo aion-restore                                  lists the backups
#   sudo aion-restore nightly-20261111-043012.dump
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 2; }
DIR="${AION_BACKUP_DIR:-/var/lib/aion/backups}"
PG_BIN="$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1)"
TOOLS="${AION_TOOLS:-/opt/aion/src/tools}"
as_aion() {
  (cd / && runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres AION_PG_BIN="$PG_BIN" PYTHONDONTWRITEBYTECODE=1 \
    AION_PG_TIMEOUT="${AION_PG_TIMEOUT:-3600}" python3 "$TOOLS/postgres_dev.py" "$@")
}

if [[ $# -ne 1 ]]; then
  echo "Usage: aion-restore <backup file>. Backups, newest first:"
  ls -1t "$DIR"/*.dump 2>/dev/null | head -30 | while read -r f; do
    printf '  %s  (%s, %s)\n' "$(basename "$f")" "$(date -r "$f" '+%Y-%m-%d %H:%M')" "$(du -h "$f" | cut -f1)"
  done
  exit 2
fi
FILE="$1"
[[ "$FILE" == */* ]] || FILE="$DIR/$FILE"
[[ -f "$FILE" ]] || { echo "No backup at $FILE" >&2; exit 1; }
NAME="$(basename "$FILE")"
[[ -t 0 ]] || { echo "Run this in a terminal: it asks for confirmation." >&2; exit 2; }

echo "This REPLACES the whole database with $NAME (made $(date -r "$FILE" '+%Y-%m-%d %H:%M'))."
echo "Every account, character and item goes back to that moment; everything since is lost"
echo "(the current database is saved first as a before-restore backup). Players are disconnected."
read -r -p "Type the backup's file name ($NAME) to continue: " ANSWER
[[ "$ANSWER" == "$NAME" ]] || { echo "Not confirmed; nothing was changed."; exit 1; }

echo "Checking that the backup restores (scratch database)..."
as_aion verify-backup "$FILE"
systemctl stop aion-host
SAFETY="$DIR/before-restore-$(date +%Y%m%d-%H%M%S).dump"
if ! as_aion backup "$SAFETY"; then
  echo "Could not save the current database first; nothing was restored. Starting the host again." >&2
  systemctl start aion-host
  exit 1
fi
echo "Current database saved to $SAFETY"
if ! as_aion restore "$FILE" --confirm-database aion; then
  echo "The restore failed and was rolled back; the database is as before. Starting the host again." >&2
  systemctl start aion-host
  exit 1
fi
systemctl start aion-host
echo "Restored $NAME. The host is starting: journalctl -u aion-host -f"
