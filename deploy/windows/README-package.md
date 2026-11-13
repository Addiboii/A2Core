# A2Core on Windows (one step)

**Install (administrator PowerShell on the new server):**
```
irm http://<server>:8091/a2core/server-setup.txt | iex
```
or download and double-click `http://<server>:8091/a2core/A2Core-Server-Setup.cmd` (it asks for administrator rights).
It downloads the bundle (checked by SHA-256), picks the fixed drive with the most free space (`X:\A2Core`), finds the
address, installs the Visual C++ runtime, PostgreSQL 18 (service `A2Core-PostgreSQL`, port 55432, local only), the game data,
the server (service `A2Core`, self-contained: no .NET install), a self-signed certificate, the firewall rules and a nightly
backup task, then starts it. Running it again skips the finished steps. Options (set before the command):
`$env:A2CORE_ROOT='E:\A2Core'`, `$env:A2CORE_ADDRESS='203.0.113.7'`, `$env:A2CORE_SOURCE='<server>:8091'`.

**Move the Linux server (same accounts and characters):** on Linux `sudo tools/windows_migration_token.sh` (stop the host
first for the final move: `sudo systemctl stop aion-host`); it prints the Windows command with a one-time token (24 h):
```
$env:A2CORE_MIGRATE_FROM='<server>'; $env:A2CORE_TOKEN='<token>'; irm http://<server>:8091/a2core/server-setup.txt | iex
```
The migration keeps the Linux certificate (the launcher keeps trusting it) and settings, with paths moved to `X:\A2Core`.

**Make a new bundle** (Linux, after changes): `tools/package_windows.sh` (writes /srv/a2core-dist, served at /a2core/).

**Windows versions:** Windows 11 (main target, the Dell 7920) and Windows 10 22H2 are the supported ones. Windows 10 1607
LTSB (build 14393, the test VM) is accepted: the setup uses only what 1607 has (PowerShell 5.1, .NET zip code instead of
tar, Invoke-WebRequest instead of curl, New-SelfSignedCertificate, Get-NetTCPConnection, Scheduled Tasks). Not verified
on 1607 and possible blockers there: (1) .NET 10 officially lists Windows 10 1607+ for the self-contained host, but LTSB
2016 is near the end of what it supports; (2) PostgreSQL 18's Windows build is tested by its makers on Windows 10/11 and
Server 2019+, not on 1607; (3) 1607 has TLS 1.2 only (no TLS 1.3), which the launcher accepts. The test run will tell.


Stage 0b of `~/a2core-log/core-upgrade.md`. The server runs as the **A2Core** Windows Service; Linux keeps working as before
(the same code: the host only calls `UseWindowsService`, which does nothing outside the Windows service manager).

## What it needs
- 64-bit Windows 10 22H2 (build 19045) or Windows 11. Nothing Windows 11-only is used: PowerShell 5.1, the built-in `tar`,
  the service manager, Windows Firewall and Scheduled Tasks are all part of Windows 10.
- .NET 10 (ASP.NET Core runtime; the SDK only where `update.ps1` builds). Supported on Windows 10 and 11; `install-package.ps1`
  installs the runtime from Microsoft (`dotnet-install.ps1`) when missing.
- PostgreSQL 18, Windows x64 installer from https://www.postgresql.org/download/windows/ (supports Windows 10 and 11).
  `install-package.ps1` runs it unattended on port 55432 with its data in `C:\ProgramData\A2Core\postgres\data`.
- Ports 443, 3700, 8087, 8091, 8780, 13328. On a VM that also runs the AION 1 servers check 443 first (IIS often has it);
  `install-package.ps1` warns about any port already in use.

## Folders
| What | Linux | Windows |
|---|---|---|
| Host build | /opt/aion/host | C:\Program Files\A2Core\host |
| Settings | /etc/aion/aion.env | service Environment (registry) + C:\ProgramData\A2Core\aion.env |
| Data, traces, backups, icons, API overrides | /var/lib/aion, /var/log/aion/traces | C:\ProgramData\A2Core\... |
| TLS certificate | /etc/aion/tls | C:\ProgramData\A2Core\tls |

The two paths that were hard-coded are settings now: `Aion:DataDir` (default `/var/lib/aion`, on Windows
`%ProgramData%\A2Core`), `Aion:ItemIconsPath` and `Aion:ApiOverridesPath` (defaults under the data folder).

## Moving the Linux server to Windows (same accounts and characters)
On the Linux VM:
```
sudo -u aion /usr/local/lib/aion/aion-backup.sh                      # a fresh dump in /var/lib/aion/backups/dump
PGPASSWORD=... pg_dumpall -h 127.0.0.1 -p 55432 -U aion_admin --roles-only > roles.sql
sudo cp /etc/aion/aion.env /etc/aion/tls/cert.pem /etc/aion/tls/key.pem /var/lib/aion/postgres/credentials.json ~/to-windows/
```
Copy `roles.sql`, the newest `aion-*.dump`, `aion.env`, `cert.pem`, `key.pem`, `credentials.json` and
`data/aion-server-data.tar.gz` to the Windows VM (scp), build the host once
(`dotnet publish server\Aion.Host -c Release -r win-x64 --self-contained false -o publish`), then as administrator:
```
powershell -ExecutionPolicy Bypass -File deploy\windows\install.ps1 -HostDir .\publish -DataBundle .\aion-server-data.tar.gz `
   -PostgresInstaller .\postgresql-18-windows-x64.exe -RolesFile .\roles.sql -DumpFile .\aion-XXXX.dump `
   -LinuxEnvFile .\aion.env -CertFile .\cert.pem -KeyFile .\key.pem -PublicAddress <the Windows VM address>
```
Put `credentials.json` into `C:\ProgramData\A2Core\postgres\`. Point the launcher at the new address. Stop the Linux server
(`sudo systemctl stop aion-host`) before taking the final dump so nothing is lost in between.

## Updating
`powershell -ExecutionPolicy Bypass -File deploy\windows\update.ps1 -Source C:\A2Core\src`: a backup, build, checks, then
the restart (nothing is touched when the checks fail).

## Status
Written 2026-11-07 on the Linux side; the host builds for win-x64 there. NOT yet run on a Windows machine: the first run on
the user's VM is the test (the Windows-side Claude runs it once the user gives access).
