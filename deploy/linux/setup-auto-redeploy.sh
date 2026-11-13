#!/usr/bin/env bash
# One-time setup for working on this server without typing a password for routine steps. Installs root-owned wrappers
# and a sudoers rule letting the given user run exactly these, nothing else:
#   /usr/local/sbin/aion-redeploy     install.sh with this server's options (tools/redeploy.sh)
#   /usr/local/sbin/aion-db-audit     the read-only database audit (tools/db_audit.py, deployed copy)
#   systemctl restart aion-host       restart the game server
# and makes /var/log/aion/traces (owner aion, group adm, 30-day cleanup) for client message traces.
#   sudo AION_PUBLIC_ADDRESS=<address> bash deploy/linux/setup-auto-redeploy.sh [user]      (default: the user who ran sudo)
#   sudo bash deploy/linux/setup-auto-redeploy.sh --remove
# Note: install.sh and the code it builds live in your home folder and run as root, so anything able to edit them as
# your user can get root through the redeploy rule. Remove it when you no longer need it.
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "run with sudo" >&2; exit 1; }
REDEPLOY=/usr/local/sbin/aion-redeploy AUDIT=/usr/local/sbin/aion-db-audit RULE=/etc/sudoers.d/aion-redeploy
TMPFILES=/etc/tmpfiles.d/aion-traces.conf
if [[ ${1:-} == --remove ]]; then rm -f "$REDEPLOY" "$AUDIT" "$RULE" "$TMPFILES"; echo "removed (traces kept in /var/log/aion)"; exit 0; fi
USER_NAME=${1:-${SUDO_USER:?pass the user name}}
REPO=$(cd "$(dirname "$0")/../.." && pwd)
PUBLIC_ADDRESS=${AION_PUBLIC_ADDRESS:?set AION_PUBLIC_ADDRESS to the address players reach this server by}

place() {   # place <path>: stdin becomes a root-owned executable
  cat > "$1.new"; chown root:root "$1.new"; chmod 755 "$1.new"; mv "$1.new" "$1"
}
place "$REDEPLOY" <<EOF
#!/bin/bash
# Redeploys the A2Core server from $REPO (setup-auto-redeploy.sh). Takes no arguments.
set -euo pipefail
cd $REPO
exec deploy/linux/install.sh --public-address $PUBLIC_ADDRESS --self-signed --skip-packages
EOF
place "$AUDIT" <<'EOF'
#!/bin/bash
# Read-only audit of the A2Core database as the service account (setup-auto-redeploy.sh). Takes no arguments.
set -euo pipefail
cd /
exec runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/db_audit.py
EOF

# Client traces: written by aion, readable by the adm group, files older than 30 days removed.
echo "d /var/log/aion/traces 2750 aion adm 30d" > "$TMPFILES"
install -d -o aion -g adm -m 755 /var/log/aion
systemd-tmpfiles --create "$TMPFILES"

cat > "$RULE.new" <<EOF
$USER_NAME ALL=(root) NOPASSWD: $REDEPLOY "", $AUDIT "", /usr/bin/systemctl restart aion-host
EOF
chmod 440 "$RULE.new"
visudo -cf "$RULE.new" >/dev/null || { rm -f "$RULE.new"; echo "sudoers rule invalid, nothing changed" >&2; exit 1; }
mv "$RULE.new" "$RULE"
echo "ok: $USER_NAME may run $REDEPLOY, $AUDIT and 'systemctl restart aion-host' without a password"
echo "ok: client traces go to /var/log/aion/traces after the next redeploy"
