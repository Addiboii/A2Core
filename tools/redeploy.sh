#!/usr/bin/env bash
# Redeploy the A2Core server: run the .NET checks on a scratch copy, then install.sh (through the root-owned
# /usr/local/sbin/aion-redeploy, allowed without a password by deploy/linux/setup-auto-redeploy.sh), then a health check.
#
#   tools/redeploy.sh               # redeploy now
#   tools/redeploy.sh --force       # redeploy now even with players online (kicks them)
#   tools/redeploy.sh --if-changed  # only if server sources changed since the last good deploy (used by the Claude
#                                   # Code Stop hook; reads the hook's JSON on stdin and answers with hook JSON)
#   tools/redeploy.sh --if-changed --force   # the Stop hook since 2026-11-07 (user rule: deploy every fix at once, even
#                                            # with players online; they are told a restart is coming)
#
# Log: ~/.local/state/aion-redeploy/last.log
set -uo pipefail

REPO=${AION_REPO:-$HOME/aion/A2Core}
STATE=${XDG_STATE_HOME:-$HOME/.local/state}/aion-redeploy
WRAPPER=/usr/local/sbin/aion-redeploy
mkdir -p "$STATE"
LOG=$STATE/last.log STAMP=$STATE/deployed.sha BUILD=$STATE/build

HOOK=0 RETRY=0 FORCE=0
for arg in "$@"; do [[ $arg == --force ]] && FORCE=1; done
if [[ " $* " == *" --if-changed "* ]]; then
  HOOK=1
  # A Stop hook that already blocked once this turn must not block again (no endless fix loop).
  [[ -t 0 ]] || RETRY=$(jq -r 'if .stop_hook_active then 1 else 0 end' 2>/dev/null || echo 0)
fi

say() {   # hook mode: a message for the user; manual mode: plain text
  if (( HOOK )); then jq -cn --arg m "$1" '{systemMessage: $m}'; else echo "$1"; fi
}
fail() {  # hook mode: hand the failure back to Claude once so it can fix it, then just report
  if (( HOOK && ! RETRY )); then echo "Auto-redeploy failed: $1 (log: $LOG)" >&2; tail -25 "$LOG" >&2; exit 2; fi
  say "Auto-redeploy failed: $1 (log: $LOG)"; exit $(( HOOK ? 0 : 1 ))
}

# Everything the deploy uses, minus build output (bin/obj, node_modules, and the web build install.sh writes into wwwroot).
fingerprint() {
  (cd "$REPO" &&
    { find server web database deploy tools/data gamedata -type f \
        -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/node_modules/*' -not -path '*/dist/*' \
        -not -path 'server/Aion.Host/wwwroot/assets/*' -not -path 'server/Aion.Host/wwwroot/index.html' \
        -not -path 'server/Aion.Host/wwwroot/admin.html' -print0
      find tools -maxdepth 1 -name '*.py' -type f -print0; } | sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1)
}

NOW=$(fingerprint)
if (( HOOK )) && [[ -f $STAMP && $(cat "$STAMP") == "$NOW" ]]; then exit 0; fi

# A restart kicks everyone in the game: the hook never deploys while a game connection is open (it runs again at the
# next stop, and a manual run asks for --force).
online=$(ss -tnH state established '( sport = :13328 )' 2>/dev/null | wc -l)
if (( online > 0 )) && (( ! FORCE )); then
  say "Auto-redeploy waiting: $online player connection(s) open; it runs once nobody is in the game"; exit 0
fi

exec 9>"$STATE/lock"
flock -n 9 || { say "Auto-redeploy skipped: another redeploy is running"; exit 0; }
# Build helpers (MSBuild worker nodes, compiler server) outlive the build and would keep the lock fd open forever.
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false

if ! sudo -n -l "$WRAPPER" >/dev/null 2>&1; then
  say "Server sources changed but auto-redeploy is not set up: run  sudo bash $REPO/deploy/linux/setup-auto-redeploy.sh  once"
  exit 0
fi

START=$(date +%s)
: > "$LOG"
echo "== $(date -Is) redeploy, sources $NOW" >> "$LOG"

# 1. Checks on a scratch copy (the tree's bin/obj belong to root after install.sh).
mkdir -p "$BUILD"
rsync -a --delete --exclude bin --exclude obj --exclude node_modules \
  "$REPO/server" "$REPO/tests" "$REPO/tools" "$REPO/research" "$REPO/database" "$REPO/gamedata" "$BUILD/" >> "$LOG" 2>&1
for f in "$REPO"/Directory.* "$REPO"/global.json; do [[ -e $f ]] && cp -f "$f" "$BUILD/"; done
echo "== build checks" >> "$LOG"
# --no-incremental: rsync keeps source mtimes, which can look older than the previous build and make it skip a rebuild.
(cd "$BUILD" && dotnet build tests/Aion.Native.Checks/Aion.Native.Checks.csproj -nologo -v q --no-incremental 9>&-) >> "$LOG" 2>&1 \
  || fail "the build failed"
echo "== run checks" >> "$LOG"
(cd "$BUILD" && timeout 900 dotnet tests/Aion.Native.Checks/bin/Debug/net10.0/Aion.Native.Checks.dll 9>&-) >> "$LOG" 2>&1 \
  || fail "a check failed: $(grep -m1 -o 'Check failed: .*' "$LOG" || echo 'see log')"

# 2. Deploy (install.sh rebuilds the host and web page, applies migrations, restarts aion-host).
echo "== install.sh" >> "$LOG"
sudo -n "$WRAPPER" >> "$LOG" 2>&1 || fail "install.sh failed"

# 3. Health: service up and the game, login and account ports listening.
echo "== health" >> "$LOG"
up=0
for _ in $(seq 1 45); do
  if systemctl is-active --quiet aion-host && ss -ltnH | grep -q ':13328 ' && ss -ltnH | grep -q ':3700 ' \
     && ss -ltnH | grep -q ':8780 '; then up=1; break; fi
  sleep 2
done
(( up )) || { journalctl -u aion-host -n 30 --no-pager -o cat >> "$LOG" 2>&1; fail "the server did not come back up"; }

echo "$NOW" > "$STAMP"
say "Server redeployed: checks passed, aion-host restarted and listening ($(( $(date +%s) - START ))s)"
