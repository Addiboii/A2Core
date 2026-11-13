#!/usr/bin/env bash
# Builds the one-step Windows server setup into the download folder served at http://<server>:8091/a2core/ (Aion:Web:WindowsSetupDir):
#   A2Core-Windows.zip     host (dotnet publish win-x64, self-contained: no .NET install needed), install-package.ps1, update.ps1
#   A2Core-Data.zip        the game data bundle (data/aion-server-data.tar.gz repacked as zip: Windows 10 1607 has no tar)
#   postgresql-18-slim.zip PostgreSQL 18 Windows binaries without pgAdmin/StackBuilder (made once, cached)
#   vc_redist.x64.exe      the Visual C++ runtime PostgreSQL needs (cached)
#   server-setup.txt       the bootstrap: irm http://<server>:8091/a2core/server-setup.txt | iex
#   A2Core-Server-Setup.cmd  the same as a double-click file (asks for administrator rights)
#   version.json           git commit, build time, file sizes and SHA-256
#   usage: tools/package_windows.sh [dist dir, default /srv/a2core-dist]
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
dist=${1:-/srv/a2core-dist}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
commit=$(git -C "$here" rev-parse --short HEAD)

echo "== host (win-x64, self-contained)"
dotnet publish "$here/server/Aion.Host" -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false \
  -o "$work/A2Core/host" -v q
mkdir -p "$work/A2Core/scripts"
cp "$here/deploy/windows/install-package.ps1" "$here/deploy/windows/update.ps1" "$here/deploy/windows/README-package.md" "$work/A2Core/scripts/"
echo "$commit" > "$work/A2Core/VERSION"
(cd "$work" && rm -f "$dist/A2Core-Windows.zip.part" && zip -qr -9 "$dist/A2Core-Windows.zip.part" A2Core && mv "$dist/A2Core-Windows.zip.part" "$dist/A2Core-Windows.zip")

data_src="$here/data/aion-server-data.tar.gz"
if [ ! -f "$dist/A2Core-Data.zip" ] || [ "$data_src" -nt "$dist/A2Core-Data.zip" ]; then
  echo "== data bundle (zip)"
  mkdir -p "$work/data" && tar -xzf "$data_src" -C "$work/data"
  (cd "$work/data" && rm -f "$dist/A2Core-Data.zip.part" && zip -qr -6 "$dist/A2Core-Data.zip.part" . && mv "$dist/A2Core-Data.zip.part" "$dist/A2Core-Data.zip")
fi
[ -f "$dist/postgresql-18-slim.zip" ] || echo "WARNING: $dist/postgresql-18-slim.zip missing (see deploy/windows/README-package.md)"
[ -f "$dist/vc_redist.x64.exe" ] || curl -sL -o "$dist/vc_redist.x64.exe" https://aka.ms/vs/17/release/vc_redist.x64.exe

echo "== setup scripts"
cp "$here/deploy/windows/server-setup.ps1" "$dist/server-setup.txt"
cp "$here/deploy/windows/A2Core-Server-Setup.cmd" "$dist/A2Core-Server-Setup.cmd"
python3 - "$dist" "$commit" <<'PY'
import hashlib, json, os, sys, datetime
dist, commit = sys.argv[1], sys.argv[2]
files = {}
for name in sorted(os.listdir(dist)):
    p = os.path.join(dist, name)
    if os.path.isfile(p) and not name.endswith('.part') and name != 'version.json':
        h = hashlib.sha256()
        with open(p, 'rb') as f:
            for chunk in iter(lambda: f.read(1 << 20), b''):
                h.update(chunk)
        files[name] = {'size': os.path.getsize(p), 'sha256': h.hexdigest()}
json.dump({'commit': commit, 'built': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'), 'files': files},
          open(os.path.join(dist, 'version.json'), 'w'), indent=1)
PY
ls -la "$dist"
