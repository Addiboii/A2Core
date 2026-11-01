#!/usr/bin/env bash
# Builds A2Core Launcher (win-x64 single-file) on Linux and installs it for player download.
# Embeds launcher.json (account URL, server address, optional self-signed cert thumbprint).
# Called from deploy/linux/install.sh and update.sh (via install.sh).
set -euo pipefail

PUBLIC_ADDRESS="" ACCOUNT_HOST="" ACCOUNT_PORT=8780 CERT="" REPO="" OUTPUT=/var/lib/aion/downloads
while [[ $# -gt 0 ]]; do
  case "$1" in
    --public-address) PUBLIC_ADDRESS="$2"; shift 2 ;;
    --account-host) ACCOUNT_HOST="$2"; shift 2 ;;
    --account-port) ACCOUNT_PORT="$2"; shift 2 ;;
    --certificate) CERT="$(realpath "$2")"; shift 2 ;;
    --repo) REPO="$(cd "$2" && pwd)"; shift 2 ;;
    --output) OUTPUT="$(realpath "$2")"; shift 2 ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
done
[[ -n "$PUBLIC_ADDRESS" ]] || { echo "--public-address is required" >&2; exit 2; }
REPO="${REPO:-$(cd "$(dirname "$0")/../.." && pwd)}"
ACCOUNT_HOST="${ACCOUNT_HOST:-$PUBLIC_ADDRESS}"
command -v dotnet &>/dev/null || { echo "dotnet SDK missing" >&2; exit 1; }

REDIRECT_DLL="$REPO/build/a2core-redirect-mingw/A2Core.Redirect.dll"
if [[ -f "$REPO/launcher/A2Core.Redirect/build-mingw.sh" ]]; then
  bash "$REPO/launcher/A2Core.Redirect/build-mingw.sh"
  [[ -f "$REDIRECT_DLL" ]] || { echo "A2Core.Redirect.dll build failed" >&2; exit 1; }
else
  echo "Warning: launcher/A2Core.Redirect/build-mingw.sh missing; publish may fall back to hosts redirect" >&2
fi

THUMBPRINT=""
if [[ -n "$CERT" ]]; then
  THUMBPRINT="$(openssl x509 -in "$CERT" -noout -fingerprint -sha1 | sed 's/^.*Fingerprint=//;s/://g' | tr '[:lower:]' '[:upper:]')"
  [[ ${#THUMBPRINT} -eq 40 ]] || { echo "Could not read certificate thumbprint from $CERT" >&2; exit 1; }
fi

LAUNCHER_JSON=$REPO/launcher/launcher.json
BACKUP="$(mktemp)"
cp "$LAUNCHER_JSON" "$BACKUP"
STAGE="$(mktemp -d)"
cleanup() {
  rm -rf "$STAGE"
  cp "$BACKUP" "$LAUNCHER_JSON"
  rm -f "$BACKUP"
}
trap cleanup EXIT

python3 - "$LAUNCHER_JSON" "$ACCOUNT_HOST" "$PUBLIC_ADDRESS" "$THUMBPRINT" "$ACCOUNT_PORT" <<'PY'
import json, sys
path, account_host, server_address, thumbprint, account_port = sys.argv[1:6]
data = {
    "name": "A2Core Launcher",
    "accountUrl": f"https://{account_host}" + ("" if account_port == "443" else f":{account_port}"),
    "serverAddress": server_address,
    "bootstrapPort": 8087,
    "savedDirSuffix": "A2CorePrivate",
}
if thumbprint:
    data["certificateThumbprint"] = thumbprint
path_obj = __import__("pathlib").Path(path)
path_obj.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
PY

dotnet publish "$REPO/launcher/AionLauncher/AionLauncher.csproj" -c Release -r win-x64 -o "$STAGE" \
  -p:EnableWindowsTargeting=true -p:DebugType=none -m:1 -p:UseSharedCompilation=false --nologo

EXE="$STAGE/A2Core Launcher.exe"
[[ -f "$EXE" ]] || { echo "Publish did not produce A2Core Launcher.exe" >&2; exit 1; }
VERSION="$(grep -m1 '<Version>' "$REPO/launcher/AionLauncher/AionLauncher.csproj" | sed 's/.*>\([^<]*\)<.*/\1/')"

install -d -o aion -g aion -m 755 "$OUTPUT"
install -o aion -g aion -m 644 "$EXE" "$OUTPUT/A2Core Launcher.exe"
printf '%s\n' "$VERSION" > "$OUTPUT/A2Core Launcher.exe.version"
chown aion:aion "$OUTPUT/A2Core Launcher.exe.version"
chmod 644 "$OUTPUT/A2Core Launcher.exe.version"
rm -f "$OUTPUT/Launcher.exe" "$OUTPUT/Launcher.exe.version"

echo "A2Core Launcher $VERSION -> $OUTPUT/A2Core Launcher.exe"
