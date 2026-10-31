# A2Core Launcher (the player program, Windows)

`A2Core Launcher.exe` (project AionLauncher) sets up a private copy of AION2 at the one build the server
supports and starts it against our server. It ships no NCSoft files: the game comes from the player's
own Steam account. One self-contained exe: downloaded, it offers to install itself for the Windows user
(`%LOCALAPPDATA%\Programs\A2Core Launcher`, desktop and Start menu shortcuts, an Apps & features entry whose
uninstall runs `--uninstall`; no administrator rights), or runs where it is ("Run without installing",
remembered). The installed copy is the one the self-update replaces.
Design and evidence: `research/client-installer.md`.

## Projects

- `Aion.Launcher.Core/`: Steam metadata (libraryfolders.vdf, appmanifest, binary appinfo.vdf launch
  arguments), the supported-build reference and file checks/placement, GamePlatform.ini, hosts and
  firewall text, the account API client. Cross-platform; tested by `tests/Aion.Launcher.Checks`.
- `AionLauncher/`: the WinForms app and its elevated helper modes.
- `supported-build.json`: every file of Steam depot 3393111 manifest 1699273097574069541 (path, size,
  SHA-256; `tools/launcher_build.py --depot-manifest <Steam>/depotcache/3393111_1699273097574069541.manifest`
  from `research/local/dev-client/verification.json`, keeping only the depot's 1218 files).
- `launcher.json`: the server (account URL, IPv4 address, bootstrap port, private saved-folder suffix).
  Embedded; a `launcher.json` next to the exe overrides it.
- `AionLauncher/icon.ico` (exe, window, taskbar, shortcuts) and `AionLauncher/emblem.png` (the rail): the "AH"
  emblem artwork (user-supplied PNG, 1254 px), cropped to the central 77 % and resized with Pillow;
  the ico holds 16-256 px.

## What it does

A guided window with four steps (Sign in, Get the game, Install, Play). Each step works out the
next one itself; the player only confirms and answers Windows prompts.

1. Sign in at the account API first (no 88 GB download for someone without an account). "Keep me signed
   in on this PC" (on by default) asks for a 30-day session and keeps its token in the settings file,
   DPAPI-encrypted for the Windows user (never the password); the next start continues without the
   sign-in page until the server ends the session or the player signs out. Unticked, the session ends
   when the launcher closes.
   Then the launcher asks the server for its launcher's hash (`GET /api/downloads/launcher/manifest`).
   When the server's file differs from the running exe, it says so and offers **Install update**; the
   player chooses ("Not now" continues). Installing downloads the file through the ticket link next to
   the exe (`A2Core Launcher.exe.new`), checks the SHA-256, renames the running exe to `A2Core Launcher.exe.old`,
   moves the download into place and starts it; the next start removes the old file. `A2Core Launcher.exe.version`
   next to the server's file (optional) is the version shown. `AION_LAUNCHER_NO_UPDATE=1` skips the check
   (development builds).
2. Get the game. If the Steam install is still the supported build it is hard-linked (same drive,
   no extra space) or copied. Otherwise the launcher downloads it from Steam itself
   (`Aion.Launcher.Core/SteamDownload.cs`, SteamKit2): the player signs in by scanning a QR code with
   the Steam mobile app (no password reaches the launcher), then the launcher asks for the depot key
   and the manifest request code of depot 3393111 manifest 1699273097574069541 and fetches every
   chunk from Steam's content servers into the private folder (16 chunks in parallel, each checked
   against its SHA-1 id; a bad chunk is fetched again from another server). The folder is
   `%LOCALAPPDATA%\A2CorePrivate\AION2` when the system drive has room, else `<drive>:\A2CorePrivate\AION2`
   on the fixed drive with the most free space. Pausing or closing keeps the download: finished
   files are listed in `.download-state`, other existing chunks are kept when their SHA-1 matches,
   and the Steam refresh token is kept (DPAPI, current Windows user) so resuming needs no new scan;
   it is deleted when the game is installed. "I already have this version in another folder" uses an
   existing copy (for example a finished Steam console `download_depot` folder) in place.
3. Install: a download is used where it is; a Steam-install source is placed on its own drive. The
   quick hash check runs (game binary and pak index files). Then, elevated once (UAC): firewall rules named "Private game server" for
   every exe in the copy, blocking all remote addresses except loopback and the server, in and out.

The reference lists only files the depot contains. A copy that has been run also holds files the
game creates: `Aion2/InstallLog.txt` and NCGuard's `Aion2/Binaries/Win64/NCGuard/bb64.dll`
(15.8 MB, not the 7.7 MB `Plugins/NCGuardSDK` copy). Whether a fresh copy creates that module while
NC's addresses point at our server is not yet tested.

Play:
1. Checks sizes (and re-hashes the quick set when those files changed).
2. Gets a launch authorization and writes
   `%LOCALAPPDATA%\AION2\Saved_A2CorePrivate\Config\Windows\GamePlatform.ini` with
   `ConfigurationServerUrl="http://<server>:8087/c/<authorization>"` (the official Saved_Steam is not touched).
3. Elevated per session (UAC): hosts lines sending the six NC hostnames to the server; the helper
   removes them when the private copy's processes exit (so the official game is not redirected otherwise).
4. Starts AION2.exe unelevated with Steam's cached launch arguments, `-saveddirsuffix=A2CorePrivate`.
The session stays signed in for further plays; it signs out when the launcher closes unless it is kept.

Uninstall (the Play page, or Apps & features) removes the firewall rules, any hosts lines, the settings,
the installed program with its shortcuts and (if chosen) the private copy.

## Client versions

The Play page has a **Client version** dropdown (dark theme, like the rest). The choice is stored as
`selectedClientVersion` in the `launcher.json` next to the exe (created from the embedded one if missing;
other keys are kept).

- `2.0.6.0` (default): the launcher's private copy, checked against `supported-build.json`, with the
  firewall rules and the optional Client Editor patch paks, exactly as before.
- `Official latest (Steam)` (`steam-latest`): the player's own Steam install, started in place and
  **read-only**: no file in the Steam folder is written, so no patch paks (they would land in its Paks
  folder), no size/hash check (it is newer than the reference) and no firewall rules (they are per exe
  path and would also block the official game). The redirect works without touching game files: the
  private `Saved_A2CorePrivate\...\GamePlatform.ini` (via `-saveddirsuffix=A2CorePrivate`) plus the
  per-session hosts lines. The folder is `clientPath` of the version, else `steamClientPath`, else the
  AION2 folder found through Steam's `libraryfolders.vdf`.

The server still runs 2.0.6.0 data and opcodes and has no client-version check, so a newer client may
start but fail or misbehave in-game. Optional keys:

```json
"steamClientPath": "Z:\\SteamLibrary\\steamapps\\common\\AION2",
"selectedClientVersion": "steam-latest",
"clientVersions": [
  { "id": "2.0.6.0", "name": "2.0.6.0", "kind": "private" },
  { "id": "steam-latest", "name": "Official latest (Steam)", "kind": "steam", "launchArgs": [] }
]
```

Each entry may set `clientPath` (its game folder) and `launchArgs` (added after Steam's arguments).
## Third-party components

SteamKit2 (LGPL-2.1, https://github.com/SteamRE/SteamKit) and QRCoder (MIT) are NuGet packages
bundled into the single-file exe. Steam's console `download_depot` (and DepotDownloader, GPL-2.0, not
used) download the same manifest.

## Build and publish

```powershell
dotnet run --project tests/Aion.Launcher.Checks
dotnet publish launcher/AionLauncher -c Release -r win-x64 -o research/local/launcher-publish -p:DebugType=none
echo 0.4.1 > "research/local/launcher-publish/A2Core Launcher.exe.version"
scp "research/local/launcher-publish/A2Core Launcher.exe" "research/local/launcher-publish/A2Core Launcher.exe.version" root@<server>:/var/lib/aion/downloads/
```

Signed-in players then see "Download A2Core Launcher" on the account page (a five-minute link from
POST /api/downloads/launcher; `/download/launcher` needs that ticket and saves as `A2Core Launcher.exe`).
Installed copies offer the new file at their next start (the version in `A2Core Launcher.exe.version` is what they show; keep
it equal to `<Version>` in AionLauncher.csproj).

## Not yet verified (CANDIDATE)

- A full run on a second PC: remote certificate acceptance by the client, Steam ownership checks
  without our dev tools, the per-session hosts helper, and the firewall rules with a remote server.
- Whether Steam still serves manifest 1699273097574069541 to `download_depot`.
- The exe is not code-signed: Windows SmartScreen warns on first start ("More info" -> "Run anyway").
