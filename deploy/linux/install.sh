#!/usr/bin/env bash
# Installs or updates the AION2 server on Ubuntu (26.04 LTS; 24.04 with extra repositories).
# Run as root from a checkout of this repository, with one way to get the account site's certificate:
#   sudo deploy/linux/install.sh --public-address 203.0.113.5 --data aion-server-data.tar.gz --domain aion.example.com --email you@example.com
#   sudo deploy/linux/install.sh --public-address 203.0.113.5 --self-signed
#   sudo deploy/linux/install.sh --public-address 203.0.113.5 --certificate cert.pem --key key.pem
#   sudo deploy/linux/install.sh --public-address 203.0.113.5 --proxy   (your own TLS proxy; see README)
# --data is needed on the first install (pack_server_data.py makes it on the development PC);
# later runs rebuild the host, apply new migrations and restart. See deploy/linux/README.md.
set -euo pipefail

ARGS=("$@")
PUBLIC_ADDRESS="" DATA_ARCHIVE="" TLS="" CERT="" KEY="" DOMAIN="" EMAIL="" SKIP_PACKAGES=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --public-address) PUBLIC_ADDRESS="$2"; shift 2 ;;
    --data) DATA_ARCHIVE="$(realpath "$2")"; shift 2 ;;
    --proxy) TLS=proxy; shift ;;
    --self-signed) TLS=self-signed; shift ;;
    --domain) TLS=letsencrypt; DOMAIN="$2"; shift 2 ;;
    --email) EMAIL="$2"; shift 2 ;;
    --certificate) TLS=files; CERT="$(realpath "$2")"; shift 2 ;;
    --key) KEY="$(realpath "$2")"; shift 2 ;;
    --skip-packages) SKIP_PACKAGES=1; shift ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 2; }
[[ -n "$PUBLIC_ADDRESS" ]] || { echo "--public-address is required" >&2; exit 2; }
[[ -n "$TLS" ]] || { echo "Choose --domain/--email, --self-signed, --certificate/--key or --proxy" >&2; exit 2; }
[[ $TLS != letsencrypt || -n "$EMAIL" ]] || { echo "--domain needs --email" >&2; exit 2; }
[[ $TLS != files || -n "$KEY" ]] || { echo "--certificate needs --key" >&2; exit 2; }

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
SRC=/opt/aion/src HOST=/opt/aion/host STATE=/var/lib/aion DATA=/var/lib/aion/data CONFIG=/etc/aion
export AION_PG_HOME=$STATE/postgres

if [[ $SKIP_PACKAGES -eq 0 ]]; then
  apt-get update
  apt-get install -y dotnet-sdk-10.0 postgresql python3 openssl nodejs npm
  [[ $TLS != letsencrypt ]] || apt-get install -y certbot
fi
dotnet --list-sdks | grep -q '^10\.' || { echo ".NET 10 SDK missing (dotnet-sdk-10.0)" >&2; exit 1; }
PG_BIN="$(ls -d /usr/lib/postgresql/*/bin | sort -V | tail -1)"
[[ -x "$PG_BIN/postgres" ]] || { echo "PostgreSQL server binaries missing" >&2; exit 1; }
command -v npm &>/dev/null || { echo "npm missing (nodejs, npm)" >&2; exit 1; }
echo "Using $(dotnet --version), node $(node --version) and $("$PG_BIN/postgres" --version)"

id aion &>/dev/null || useradd --system --home-dir $STATE --shell /usr/sbin/nologin aion
install -d -o aion -g aion -m 750 $STATE $DATA $STATE/ncguard
install -d -o aion -g aion -m 755 $STATE/downloads
install -d -o root -g aion -m 750 $CONFIG
install -d -m 755 /opt/aion

# Server data (bootstrap and NCGuard fixtures, terrain, navmesh, catalogue).
if [[ -n "$DATA_ARCHIVE" ]]; then
  tar -xzf "$DATA_ARCHIVE" -C $DATA --no-same-owner
  chown -R aion:aion $DATA
fi
[[ -f $DATA/bootstrap/keys/app-key.json ]] || { echo "No server data in $DATA; pass --data" >&2; exit 1; }

# Database tools and migrations (the service account must read them), then the host.
rm -rf $SRC && install -d -m 755 $SRC
cp -r "$REPO/tools" "$REPO/database" $SRC/
# Account page and staff dashboard (React) into wwwroot first, so the publish includes them.
(cd "$REPO/web" && npm ci --no-audit --no-fund && npm run build)
dotnet publish "$REPO/server/Aion.Host/Aion.Host.csproj" -c Release -o $HOST.new --nologo
rm -rf $HOST && mv $HOST.new $HOST

as_aion() { cd / && runuser -u aion -- env AION_PG_HOME="$AION_PG_HOME" AION_PG_BIN="$PG_BIN" "$@"; }
FIRST=0
[[ -f $AION_PG_HOME/credentials.json ]] || FIRST=1
systemctl stop aion-host 2>/dev/null || true
if systemctl is-active --quiet aion-postgres; then
  # Running under systemd: apply new migrations in place.
  as_aion python3 $SRC/tools/postgres_dev.py migrate
else
  # setup creates the cluster (first run), starts it with pg_ctl and applies migrations.
  as_aion python3 $SRC/tools/postgres_dev.py setup
  if [[ $FIRST -eq 1 ]]; then
    as_aion python3 $SRC/tools/postgres_dev.py import-catalog --directory $DATA/game-catalog-v2
    as_aion python3 $SRC/tools/configure_starters.py
    as_aion python3 $SRC/tools/class_skills.py install
    as_aion python3 $SRC/tools/equipment_rules.py
  fi
  as_aion python3 $SRC/tools/postgres_dev.py stop
fi

# The account site's certificate, readable by the service: /etc/aion/tls/{cert,key}.pem.
TLS_DIR=$CONFIG/tls
install -d -o root -g aion -m 750 $TLS_DIR
place_tls() { install -o root -g aion -m 640 "$1" $TLS_DIR/cert.pem; install -o root -g aion -m 640 "$2" $TLS_DIR/key.pem; }
case $TLS in
  files) place_tls "$CERT" "$KEY" ;;
  self-signed)
    if [[ ! -f $TLS_DIR/cert.pem ]]; then
      openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=$PUBLIC_ADDRESS"         -addext "subjectAltName=IP:$PUBLIC_ADDRESS" -keyout $TLS_DIR/key.pem -out $TLS_DIR/cert.pem
      chown root:aion $TLS_DIR/*.pem && chmod 640 $TLS_DIR/*.pem
    fi ;;
  letsencrypt)
    # HTTP-01 on port 80 (443 belongs to the NCGuard sink). Renewals copy the files and restart the host.
    HOOK=/etc/letsencrypt/renewal-hooks/deploy/aion.sh
    install -d /etc/letsencrypt/renewal-hooks/deploy
    printf '#!/bin/sh
install -o root -g aion -m 640 /etc/letsencrypt/live/%s/fullchain.pem %s/cert.pem
install -o root -g aion -m 640 /etc/letsencrypt/live/%s/privkey.pem %s/key.pem
systemctl try-restart aion-host
'       "$DOMAIN" "$TLS_DIR" "$DOMAIN" "$TLS_DIR" > $HOOK
    chmod 755 $HOOK
    if command -v ufw &>/dev/null && ufw status | grep -q "Status: active"; then ufw allow 80/tcp; fi
    [[ -d /etc/letsencrypt/live/$DOMAIN ]] || certbot certonly --standalone --non-interactive --agree-tos -m "$EMAIL" -d "$DOMAIN"
    place_tls /etc/letsencrypt/live/$DOMAIN/fullchain.pem /etc/letsencrypt/live/$DOMAIN/privkey.pem ;;
esac
if [[ $TLS == proxy ]]; then TLS_ARGS=(--proxy); else TLS_ARGS=(--certificate $TLS_DIR/cert.pem --key $TLS_DIR/key.pem); fi

# Keep the staff dashboard's private address across updates.
STAFF_PATH="$(grep -s '^Aion__Web__StaffPath=' $CONFIG/aion.env | cut -d= -f2- || true)"
[[ -n "$STAFF_PATH" ]] && TLS_ARGS+=(--staff-path "$STAFF_PATH")
as_aion python3 $SRC/tools/server_env.py --public-address "$PUBLIC_ADDRESS" --data $DATA --state $STATE "${TLS_ARGS[@]}" > $CONFIG/aion.env.new
chown root:aion $CONFIG/aion.env.new && chmod 640 $CONFIG/aion.env.new && mv $CONFIG/aion.env.new $CONFIG/aion.env

sed "s#@PG_BIN@#$PG_BIN#" "$REPO/deploy/linux/aion-postgres.service" > /etc/systemd/system/aion-postgres.service
cp "$REPO/deploy/linux/aion-host.service" /etc/systemd/system/aion-host.service
systemctl daemon-reload
systemctl enable --now aion-postgres
systemctl enable aion-host
systemctl restart aion-host

if command -v ufw &>/dev/null && ufw status | grep -q "Status: active"; then
  for port in 443 3700 8087 8091 13328; do ufw allow $port/tcp; done
  [[ $TLS == proxy ]] || ufw allow 8780/tcp
fi
# update.sh repeats this install with the same options (without --data: the data stays unpacked).
SAVED=()
for ((i = 0; i < ${#ARGS[@]}; i++)); do
  if [[ ${ARGS[i]} == --data ]]; then ((i++)); continue; fi
  [[ ${ARGS[i]} == --skip-packages ]] && continue
  SAVED+=("${ARGS[i]}")
done
printf '%q ' "${SAVED[@]}" > $CONFIG/install-args && chmod 640 $CONFIG/install-args
echo "Installed. Status: systemctl status aion-host; logs: journalctl -u aion-host -f"
echo "Staff dashboard: $(grep '^Aion__Web__StaffPath=' $CONFIG/aion.env | cut -d= -f2-) on the account site (port 8780)"
