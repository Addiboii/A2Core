#!/usr/bin/env bash
# Updates the server from GitHub: database backup, git pull, then install.sh with the options of the
# last install (/etc/aion/install-args). Run as root from the repository checkout:
#   sudo deploy/linux/update.sh
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 2; }
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
[[ -f /etc/aion/install-args ]] || { echo "No previous install; run deploy/linux/install.sh first." >&2; exit 1; }

# Players online get a chat warning and a minute before the restart (AION_UPDATE_NOW=1 skips the wait).
CHAT_PORT="${AION_CHAT_PORT:-8091}"
ONLINE="$(curl -fsS --max-time 5 "http://127.0.0.1:$CHAT_PORT/internal/online" 2>/dev/null | sed -n 's/.*"players":\([0-9]*\).*/\1/p' || true)"
if [[ "${ONLINE:-0}" -gt 0 && "${AION_UPDATE_NOW:-0}" != 1 ]]; then
  echo "$ONLINE player(s) online: warning them, restarting in 60 seconds."
  curl -fsS --max-time 5 -X POST --data "The server restarts in 1 minute for an update. Please find a safe spot." "http://127.0.0.1:$CHAT_PORT/internal/announce" >/dev/null || true
  sleep 50
  curl -fsS --max-time 5 -X POST --data "The server restarts in 10 seconds. Back in about a minute." "http://127.0.0.1:$CHAT_PORT/internal/announce" >/dev/null || true
  sleep 10
fi

# Back up before new migrations run (kept in /var/lib/aion/backups; the newest 10 are kept).
install -d -o aion -g aion -m 750 /var/lib/aion/backups
BACKUP=/var/lib/aion/backups/before-update-$(date +%Y%m%d-%H%M%S).dump
(cd / && runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres \
  AION_PG_BIN="$(ls -d /usr/lib/postgresql/*/bin | sort -V | tail -1)" \
  python3 /opt/aion/src/tools/postgres_dev.py backup "$BACKUP")
ls -1t /var/lib/aion/backups/*.dump | tail -n +11 | xargs -r rm --
echo "Database backed up to $BACKUP"

# The checkout belongs to the user who cloned it; pull as that user.
OWNER="$(stat -c %U "$REPO")"
runuser -u "$OWNER" -- git -C "$REPO" pull --ff-only
eval "set -- $(cat /etc/aion/install-args)"
# Packages are re-checked (new ones, e.g. nodejs/npm, are installed; present ones are a no-op).
exec "$REPO/deploy/linux/install.sh" "$@"
